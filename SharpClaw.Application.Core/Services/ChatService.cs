using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed partial class ChatService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    ProviderApiClientFactory clientFactory,
    IHttpClientFactory httpClientFactory,
    AgentJobService jobService)
{
    private const int MaxHistoryMessages = 50;

    /// <summary>
    /// Maximum number of tool-call round-trips before forcing a final
    /// response.  Prevents infinite loops when the model keeps emitting
    /// tool calls.
    /// </summary>
    private const int MaxToolCallRounds = 10;

    public async Task<ChatResponse> SendMessageAsync(
        Guid channelId, ChatRequest request,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback = null,
        CancellationToken ct = default)
    {
        var channel = await db.Channels
            .Include(c => c.Agent!).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct)
            ?? throw new ArgumentException($"Channel {channelId} not found.");

        var agent = ResolveAgent(channel, request.AgentId);
        var model = agent.Model;
        var provider = model.Provider;

        if (string.IsNullOrEmpty(provider.EncryptedApiKey))
            throw new InvalidOperationException("Provider does not have an API key configured.");

        // Build channel history: recent messages + new user message
        var history = await db.ChatMessages
            .Where(m => m.ChannelId == channelId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxHistoryMessages)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatCompletionMessage(m.Role, m.Content))
            .ToListAsync(ct);

        history.Add(new ChatCompletionMessage("user", request.Message));

        var apiKey = ApiKeyEncryptor.Decrypt(provider.EncryptedApiKey, encryptionOptions.Key);
        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);
        var useNativeTools = client.SupportsNativeToolCalling;
        var systemPrompt = BuildSystemPrompt(agent.SystemPrompt, useNativeTools);

        // Build chat header for the user message (if enabled)
        var chatHeader = await BuildChatHeaderAsync(channel, request.ClientType, ct);
        var messageForModel = chatHeader is not null
            ? chatHeader + request.Message
            : request.Message;

        // Replace last history entry with the header-prefixed version for model
        history[^1] = new ChatCompletionMessage("user", messageForModel);

        using var httpClient = httpClientFactory.CreateClient();

        var loopResult = useNativeTools
            ? await RunNativeToolLoopAsync(
                client, httpClient, apiKey, model.Name, systemPrompt,
                history, agent.Id, channelId, approvalCallback, ct)
            : await RunTextToolLoopAsync(
                client, httpClient, apiKey, model.Name, systemPrompt,
                history, agent.Id, channelId, approvalCallback, ct);

        // Persist both messages
        var userMessage = new ChatMessageDB
        {
            Role = "user",
            Content = request.Message,
            ChannelId = channelId
        };

        var assistantMessage = new ChatMessageDB
        {
            Role = "assistant",
            Content = loopResult.AssistantContent,
            ChannelId = channelId
        };

        db.ChatMessages.Add(userMessage);
        db.ChatMessages.Add(assistantMessage);
        await db.SaveChangesAsync(ct);

        return new ChatResponse(
            new ChatMessageResponse(userMessage.Role, userMessage.Content, userMessage.CreatedAt),
            new ChatMessageResponse(assistantMessage.Role, assistantMessage.Content, assistantMessage.CreatedAt),
            loopResult.JobResults.Count > 0 ? loopResult.JobResults : null);
    }

    public async Task<IReadOnlyList<ChatMessageResponse>> GetHistoryAsync(
        Guid channelId, int limit = 50, CancellationToken ct = default)
    {
        return await db.ChatMessages
            .Where(m => m.ChannelId == channelId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessageResponse(m.Role, m.Content, m.CreatedAt))
            .ToListAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Agent resolution
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the effective agent for a channel operation.  If no
    /// override is specified, the channel's default agent is used.
    /// If the channel has no default agent, the context's agent is
    /// used as fallback.  When an override is specified it must be
    /// the default agent or one of the channel's allowed agents
    /// (falling back to the context's allowed agents when the channel
    /// has none).
    /// </summary>
    private static AgentDB ResolveAgent(ChannelDB channel, Guid? requestedAgentId)
    {
        var defaultAgent = channel.Agent ?? channel.AgentContext?.Agent;

        if (requestedAgentId is null || requestedAgentId == defaultAgent?.Id)
            return defaultAgent
                ?? throw new InvalidOperationException(
                    $"Channel {channel.Id} has no agent and no context agent.");

        // Check channel-level allowed agents first, then context-level.
        var effectiveAllowed = channel.AllowedAgents.Count > 0
            ? channel.AllowedAgents
            : (IEnumerable<AgentDB>)(channel.AgentContext?.AllowedAgents ?? []);

        var allowed = effectiveAllowed.FirstOrDefault(a => a.Id == requestedAgentId);
        return allowed
            ?? throw new InvalidOperationException(
                $"Agent {requestedAgentId} is not allowed on channel {channel.Id}. " +
                "Add it to the channel's or context's allowed agents first.");
    }

    // ═══════════════════════════════════════════════════════════════
    // Chat header
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a compact metadata header that is prepended to the user
    /// message content so the agent knows who is talking.  Returns
    /// <see langword="null"/> when headers are disabled for the channel
    /// (either at channel level or inherited from the context).
    /// </summary>
    private async Task<string?> BuildChatHeaderAsync(
        ChannelDB channel, ChatClientType clientType, CancellationToken ct)
    {
        // Channel-level flag takes precedence; fall back to context.
        var disabled = channel.DisableChatHeader
            || (channel.AgentContext?.DisableChatHeader ?? false);

        if (disabled)
            return null;

        var userId = jobService.GetSessionUserId();
        if (userId is null)
            return null;

        var user = await db.Users
            .Include(u => u.Role)
            .ThenInclude(r => r!.PermissionSet)
            .ThenInclude(ps => ps!.DangerousShellAccesses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return null;

        // Load remaining grant collections if the user has a permission set
        PermissionSetDB? ps = null;
        if (user.Role?.PermissionSetId is { } psId)
        {
            ps = await db.PermissionSets
                .Include(p => p.SafeShellAccesses)
                .Include(p => p.ContainerAccesses)
                .Include(p => p.WebsiteAccesses)
                .Include(p => p.SearchEngineAccesses)
                .Include(p => p.LocalInfoStorePermissions)
                .Include(p => p.ExternalInfoStorePermissions)
                .Include(p => p.AudioDeviceAccesses)
                .Include(p => p.AgentPermissions)
                .Include(p => p.TaskPermissions)
                .Include(p => p.SkillPermissions)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == psId, ct);
        }

        var sb = new StringBuilder();
        sb.Append("[user: ").Append(user.Username);
        sb.Append(" | via: ").Append(clientType);

        if (user.Role is not null && ps is not null)
        {
            var grants = new List<string>();
            if (ps.CanCreateSubAgents) grants.Add("CreateSubAgents");
            if (ps.CanCreateContainers) grants.Add("CreateContainers");
            if (ps.CanRegisterInfoStores) grants.Add("RegisterInfoStores");
            if (ps.CanAccessLocalhostInBrowser) grants.Add("LocalhostBrowser");
            if (ps.CanAccessLocalhostCli) grants.Add("LocalhostCli");
            if (ps.DangerousShellAccesses.Count > 0) grants.Add("DangerousShell");
            if (ps.SafeShellAccesses.Count > 0) grants.Add("SafeShell");
            if (ps.ContainerAccesses.Count > 0) grants.Add("ContainerAccess");
            if (ps.WebsiteAccesses.Count > 0) grants.Add("WebsiteAccess");
            if (ps.SearchEngineAccesses.Count > 0) grants.Add("SearchEngineAccess");
            if (ps.LocalInfoStorePermissions.Count > 0) grants.Add("LocalInfoStore");
            if (ps.ExternalInfoStorePermissions.Count > 0) grants.Add("ExternalInfoStore");
            if (ps.AudioDeviceAccesses.Count > 0) grants.Add("AudioDevice");
            if (ps.AgentPermissions.Count > 0) grants.Add("ManageAgent");
            if (ps.TaskPermissions.Count > 0) grants.Add("EditTask");
            if (ps.SkillPermissions.Count > 0) grants.Add("AccessSkill");

            if (grants.Count > 0)
                sb.Append(" | role: ").Append(user.Role.Name)
                  .Append(" (").Append(string.Join(", ", grants)).Append(')');
            else
                sb.Append(" | role: ").Append(user.Role.Name);
        }

        if (!string.IsNullOrWhiteSpace(user.Bio))
            sb.Append(" | bio: ").Append(user.Bio);

        sb.AppendLine("]");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // Streaming chat
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Streams a chat response token-by-token, executing tool calls
    /// inline. When a job requires approval:
    /// <list type="bullet">
    ///   <item>If the session user can approve, emits
    ///         <see cref="ChatStreamEventType.ApprovalRequired"/> and
    ///         calls <paramref name="approvalCallback"/> to get the
    ///         decision (y/n in CLI, bool in API).</item>
    ///   <item>If the session user cannot approve, the job is
    ///         automatically denied and execution continues.</item>
    /// </list>
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> SendMessageStreamAsync(
        Guid channelId,
        ChatRequest request,
        Func<AgentJobResponse, CancellationToken, Task<bool>> approvalCallback,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = await db.Channels
            .Include(c => c.Agent!).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct)
            ?? throw new ArgumentException($"Channel {channelId} not found.");

        var agent = ResolveAgent(channel, request.AgentId);
        var model = agent.Model;
        var provider = model.Provider;

        if (string.IsNullOrEmpty(provider.EncryptedApiKey))
            throw new InvalidOperationException("Provider does not have an API key configured.");

        var history = await db.ChatMessages
            .Where(m => m.ChannelId == channelId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxHistoryMessages)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatCompletionMessage(m.Role, m.Content))
            .ToListAsync(ct);

        history.Add(new ChatCompletionMessage("user", request.Message));

        var apiKey = ApiKeyEncryptor.Decrypt(provider.EncryptedApiKey, encryptionOptions.Key);
        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);
        var systemPrompt = BuildSystemPrompt(agent.SystemPrompt, nativeToolCalling: true);

        // Build chat header for the user message (if enabled)
        var chatHeader = await BuildChatHeaderAsync(channel, request.ClientType, ct);
        if (chatHeader is not null)
            history[^1] = new ChatCompletionMessage("user", chatHeader + request.Message);

        using var httpClient = httpClientFactory.CreateClient();

        // Convert history to tool-aware messages
        var messages = new List<ToolAwareMessage>(history.Count);
        foreach (var msg in history)
            messages.Add(new ToolAwareMessage { Role = msg.Role, Content = msg.Content });

        var jobResults = new List<AgentJobResponse>();
        var fullContent = new StringBuilder();
        var rounds = 0;

        while (true)
        {
            // Stream the current round
            ChatCompletionResult? roundResult = null;

            await foreach (var chunk in client.StreamChatCompletionWithToolsAsync(
                httpClient, apiKey, model.Name, systemPrompt, messages, AllTools, ct))
            {
                if (chunk.Delta is not null)
                    yield return ChatStreamEvent.TextDelta(chunk.Delta);

                if (chunk.IsFinished)
                    roundResult = chunk.Finished;
            }

            if (roundResult is null)
                break;

            fullContent.Append(roundResult.Content ?? "");

            if (!roundResult.HasToolCalls || ++rounds > MaxToolCallRounds)
                break;

            // Record assistant turn with tool calls
            messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                roundResult.ToolCalls, roundResult.Content));

            // Reset content for next round (tool results will produce new text)
            fullContent.Clear();

            foreach (var tc in roundResult.ToolCalls)
            {
                var parsed = ParseNativeToolCall(tc);
                if (parsed is null)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id,
                        "Error: unrecognized tool or malformed arguments."));
                    continue;
                }

                var jobRequest = await BuildJobRequestAsync(parsed, agent.Id, ct);
                var jobResponse = await jobService.SubmitAsync(channelId, jobRequest, ct);

                // ── Inline approval ───────────────────────────────
                if (jobResponse.Status == AgentJobStatus.AwaitingApproval)
                {
                    // Check if the session user CAN approve
                    var canApprove = await CanSessionUserApproveAsync(
                        agent.Id, jobRequest.ActionType, jobRequest.ResourceId, ct);

                    if (canApprove)
                    {
                        yield return ChatStreamEvent.NeedsApproval(jobResponse);

                        var approved = await approvalCallback(jobResponse, ct);

                        if (approved)
                        {
                            jobResponse = await jobService.ApproveAsync(jobResponse.Id, new ApproveAgentJobRequest(), ct)
                                ?? jobResponse;
                        }
                        else
                        {
                            jobResponse = await jobService.CancelAsync(jobResponse.Id, ct)
                                ?? jobResponse;
                        }
                    }
                    else
                    {
                        // Auto-deny: user cannot approve
                        jobResponse = await jobService.CancelAsync(jobResponse.Id, ct)
                            ?? jobResponse;
                    }

                    yield return ChatStreamEvent.ApprovalDecision(jobResponse);
                }
                else
                {
                    yield return ChatStreamEvent.ToolStart(jobResponse);
                }

                jobResults.Add(jobResponse);

                var resultContent =
                    $"status={jobResponse.Status}" +
                    (jobResponse.ResultData is not null ? $" result={jobResponse.ResultData}" : "") +
                    (jobResponse.ErrorLog is not null ? $" error={jobResponse.ErrorLog}" : "");

                messages.Add(ToolAwareMessage.ToolResult(tc.Id, resultContent));
            }
        }

        // Persist both messages
        var assistantContent = fullContent.ToString();

        var userMessage = new ChatMessageDB
        {
            Role = "user",
            Content = request.Message,
            ChannelId = channelId
        };

        var assistantMessage = new ChatMessageDB
        {
            Role = "assistant",
            Content = assistantContent,
            ChannelId = channelId
        };

        db.ChatMessages.Add(userMessage);
        db.ChatMessages.Add(assistantMessage);
        await db.SaveChangesAsync(ct);

        yield return ChatStreamEvent.Complete(new ChatResponse(
            new ChatMessageResponse(userMessage.Role, userMessage.Content, userMessage.CreatedAt),
            new ChatMessageResponse(assistantMessage.Role, assistantMessage.Content, assistantMessage.CreatedAt),
            jobResults.Count > 0 ? jobResults : null));
    }

    /// <summary>
    /// Checks whether the current session user has sufficient authority
    /// to approve the given action — i.e. their own permission check
    /// would return <see cref="ClearanceVerdict.Approved"/>.
    /// </summary>
    private async Task<bool> CanSessionUserApproveAsync(
        Guid agentId, AgentActionType actionType, Guid? resourceId,
        CancellationToken ct)
    {
        var userId = jobService.GetSessionUserId();
        if (userId is null) return false;

        var caller = new ActionCaller(UserId: userId);
        var result = await jobService.CheckPermissionAsync(
            agentId, actionType, resourceId, caller, ct);

        return result.Verdict == ClearanceVerdict.Approved;
    }

    /// <summary>
    /// Resolves the container GUID from a <see cref="ParsedToolCall"/>.
    /// If the model provided a valid GUID it is used as-is; otherwise the
    /// container is looked up by <c>SandboxName</c>.
    /// </summary>
    private async Task<Guid?> ResolveContainerIdAsync(
        ParsedToolCall parsed, CancellationToken ct)
    {
        if (parsed.ResourceId.HasValue)
            return parsed.ResourceId;

        if (parsed.SandboxId is not null)
        {
            var container = await db.Containers
                .FirstOrDefaultAsync(c => c.SandboxName == parsed.SandboxId, ct);
            return container?.Id;
        }

        return null;
    }

    /// <summary>
    /// Builds a <see cref="SubmitAgentJobRequest"/> from a parsed tool call.
    /// For <see cref="AgentActionType.ExecuteAsSafeShell"/> the container
    /// is resolved by sandbox name when no GUID is provided.
    /// </summary>
    private async Task<SubmitAgentJobRequest> BuildJobRequestAsync(
        ParsedToolCall parsed, Guid agentId, CancellationToken ct)
    {
        var resourceId = parsed.ActionType is AgentActionType.ExecuteAsSafeShell
            ? await ResolveContainerIdAsync(parsed, ct)
            : parsed.ResourceId;

        return new SubmitAgentJobRequest(
            ActionType: parsed.ActionType,
            ResourceId: resourceId,
            CallerAgentId: agentId,
            DangerousShellType: parsed.DangerousShellType,
            SafeShellType: parsed.SafeShellType,
            ScriptJson: parsed.ScriptJson,
            WorkingDirectory: parsed.WorkingDirectory,
            TranscriptionModelId: parsed.TranscriptionModelId,
            Language: parsed.Language);
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool-call loop implementations
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Uses native provider function calling. The provider returns structured
    /// tool calls that are dispatched through the job pipeline, with results
    /// fed back as <c>tool</c>-role messages.
    /// </summary>
    private async Task<ToolLoopResult> RunNativeToolLoopAsync(
        IProviderApiClient client,
        HttpClient httpClient,
        string apiKey,
        string modelName,
        string? systemPrompt,
        IReadOnlyList<ChatCompletionMessage> dbHistory,
        Guid agentId,
        Guid channelId,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
        CancellationToken ct)
    {
        var messages = new List<ToolAwareMessage>(dbHistory.Count);
        foreach (var msg in dbHistory)
            messages.Add(new ToolAwareMessage { Role = msg.Role, Content = msg.Content });

        var jobResults = new List<AgentJobResponse>();
        var rounds = 0;

        while (true)
        {
            var result = await client.ChatCompletionWithToolsAsync(
                httpClient, apiKey, modelName, systemPrompt, messages, AllTools, ct);

            if (!result.HasToolCalls || ++rounds > MaxToolCallRounds)
                return new ToolLoopResult(result.Content ?? "", jobResults);

            // Record assistant turn with tool calls
            messages.Add(ToolAwareMessage.AssistantWithToolCalls(result.ToolCalls, result.Content));

            var anyUnresolvableApproval = false;

            foreach (var tc in result.ToolCalls)
            {
                var parsed = ParseNativeToolCall(tc);
                if (parsed is null)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id,
                        "Error: unrecognized tool or malformed arguments."));
                    continue;
                }

                var jobRequest = await BuildJobRequestAsync(parsed, agentId, ct);

                var jobResponse = await jobService.SubmitAsync(channelId, jobRequest, ct);

                // ── Inline approval (when callback available) ────
                if (jobResponse.Status == AgentJobStatus.AwaitingApproval
                    && approvalCallback is not null)
                {
                    var canApprove = await CanSessionUserApproveAsync(
                        agentId, jobRequest.ActionType, jobRequest.ResourceId, ct);

                    if (canApprove)
                    {
                        var approved = await approvalCallback(jobResponse, ct);
                        jobResponse = approved
                            ? await jobService.ApproveAsync(jobResponse.Id, new ApproveAgentJobRequest(), ct) ?? jobResponse
                            : await jobService.CancelAsync(jobResponse.Id, ct) ?? jobResponse;
                    }
                    else
                    {
                        jobResponse = await jobService.CancelAsync(jobResponse.Id, ct) ?? jobResponse;
                    }
                }

                jobResults.Add(jobResponse);

                var resultContent =
                    $"status={jobResponse.Status}" +
                    (jobResponse.ResultData is not null ? $" result={jobResponse.ResultData}" : "") +
                    (jobResponse.ErrorLog is not null ? $" error={jobResponse.ErrorLog}" : "");

                messages.Add(ToolAwareMessage.ToolResult(tc.Id, resultContent));

                if (jobResponse.Status == AgentJobStatus.AwaitingApproval)
                    anyUnresolvableApproval = true;
            }

            if (anyUnresolvableApproval)
            {
                var finalResult = await client.ChatCompletionWithToolsAsync(
                    httpClient, apiKey, modelName, systemPrompt, messages, AllTools, ct);
                return new ToolLoopResult(finalResult.Content ?? "", jobResults);
            }
        }
    }

    /// <summary>
    /// Text-based fallback for providers that lack native tool calling.
    /// The model is instructed via the system prompt to emit
    /// <c>[TOOL_CALL:...]</c> markers which are parsed post-hoc.
    /// </summary>
    private async Task<ToolLoopResult> RunTextToolLoopAsync(
        IProviderApiClient client,
        HttpClient httpClient,
        string apiKey,
        string modelName,
        string systemPrompt,
        List<ChatCompletionMessage> history,
        Guid agentId,
        Guid channelId,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
        CancellationToken ct)
    {
        var jobResults = new List<AgentJobResponse>();
        string assistantContent;
        var rounds = 0;

        while (true)
        {
            assistantContent = await client.ChatCompletionAsync(
                httpClient, apiKey, modelName, systemPrompt, history, ct);

            var toolCalls = ParseToolCalls(assistantContent);
            if (toolCalls.Count == 0 || ++rounds > MaxToolCallRounds)
                break;

            history.Add(new ChatCompletionMessage("assistant", assistantContent));

            var toolResultBuilder = new StringBuilder();
            var anyUnresolvableApproval = false;

            foreach (var call in toolCalls)
            {
                var jobRequest = await BuildJobRequestAsync(call, agentId, ct);

                var jobResponse = await jobService.SubmitAsync(channelId, jobRequest, ct);

                // ── Inline approval (when callback available) ────
                if (jobResponse.Status == AgentJobStatus.AwaitingApproval
                    && approvalCallback is not null)
                {
                    var canApprove = await CanSessionUserApproveAsync(
                        agentId, jobRequest.ActionType, jobRequest.ResourceId, ct);

                    if (canApprove)
                    {
                        var approved = await approvalCallback(jobResponse, ct);
                        jobResponse = approved
                            ? await jobService.ApproveAsync(jobResponse.Id, new ApproveAgentJobRequest(), ct) ?? jobResponse
                            : await jobService.CancelAsync(jobResponse.Id, ct) ?? jobResponse;
                    }
                    else
                    {
                        jobResponse = await jobService.CancelAsync(jobResponse.Id, ct) ?? jobResponse;
                    }
                }

                jobResults.Add(jobResponse);

                toolResultBuilder.AppendLine(
                    $"[TOOL_RESULT:{call.CallId}] status={jobResponse.Status}" +
                    (jobResponse.ResultData is not null ? $" result={jobResponse.ResultData}" : "") +
                    (jobResponse.ErrorLog is not null ? $" error={jobResponse.ErrorLog}" : ""));

                if (jobResponse.Status == AgentJobStatus.AwaitingApproval)
                    anyUnresolvableApproval = true;
            }

            history.Add(new ChatCompletionMessage("user", toolResultBuilder.ToString()));

            if (anyUnresolvableApproval)
            {
                assistantContent = await client.ChatCompletionAsync(
                    httpClient, apiKey, modelName, systemPrompt, history, ct);
                break;
            }
        }

        assistantContent = StripToolCallBlocks(assistantContent);
        return new ToolLoopResult(assistantContent, jobResults);
    }

    /// <summary>
    /// Parses a native <see cref="ChatToolCall"/> into the internal
    /// <see cref="ParsedToolCall"/> representation. Returns <see langword="null"/>
    /// if the tool name is unrecognized or the arguments are malformed.
    /// </summary>
    private static ParsedToolCall? ParseNativeToolCall(ChatToolCall toolCall)
    {
        if (!ToolNameToActionType.TryGetValue(toolCall.Name, out var actionType))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<ToolCallPayload>(toolCall.ArgumentsJson, JsonOptions);
            if (payload is null) return null;

            Guid? resourceId = Guid.TryParse(payload.ResourceId, out var rid) ? rid : null;
            // TargetId is the generic "resourceId" alias for non-shell tools
            resourceId ??= Guid.TryParse(payload.TargetId, out var tid) ? tid : null;

            Guid? transcriptionModelId = Guid.TryParse(payload.TranscriptionModelId, out var tmid) ? tmid : null;

            DangerousShellType? dangerousShell = Enum.TryParse<DangerousShellType>(
                payload.ShellType, ignoreCase: true, out var ds) ? ds : null;

            // For non-shell/non-transcription actions, pass the full
            // arguments JSON as ScriptJson so DispatchExecutionAsync
            // can deserialize action-specific fields from it.
            var scriptJson = actionType switch
            {
                AgentActionType.ExecuteAsSafeShell
                    => payload.Script is { } script ? script.GetRawText() : null,
                AgentActionType.UnsafeExecuteAsDangerousShell
                    => payload.Command,
                _ => toolCall.ArgumentsJson,
            };

            return new ParsedToolCall(
                toolCall.Id,
                actionType,
                resourceId,
                payload.SandboxId,
                scriptJson,
                dangerousShell,
                actionType == AgentActionType.ExecuteAsSafeShell ? SafeShellType.Mk8Shell : null,
                transcriptionModelId,
                payload.Language,
                payload.WorkingDirectory);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps native tool function names to their <see cref="AgentActionType"/>.
    /// </summary>
    private static readonly Dictionary<string, AgentActionType> ToolNameToActionType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["execute_mk8_shell"]              = AgentActionType.ExecuteAsSafeShell,
        ["execute_dangerous_shell"]        = AgentActionType.UnsafeExecuteAsDangerousShell,
        ["create_sub_agent"]               = AgentActionType.CreateSubAgent,
        ["create_container"]               = AgentActionType.CreateContainer,
        ["register_info_store"]            = AgentActionType.RegisterInfoStore,
        ["access_localhost_in_browser"]    = AgentActionType.AccessLocalhostInBrowser,
        ["access_localhost_cli"]           = AgentActionType.AccessLocalhostCli,
        ["access_local_info_store"]        = AgentActionType.AccessLocalInfoStore,
        ["access_external_info_store"]     = AgentActionType.AccessExternalInfoStore,
        ["access_website"]                 = AgentActionType.AccessWebsite,
        ["query_search_engine"]            = AgentActionType.QuerySearchEngine,
        ["access_container"]               = AgentActionType.AccessContainer,
        ["manage_agent"]                   = AgentActionType.ManageAgent,
        ["edit_task"]                       = AgentActionType.EditTask,
        ["access_skill"]                   = AgentActionType.AccessSkill,
        ["transcribe_from_audio_device"]   = AgentActionType.TranscribeFromAudioDevice,
        ["transcribe_from_audio_stream"]   = AgentActionType.TranscribeFromAudioStream,
        ["transcribe_from_audio_file"]     = AgentActionType.TranscribeFromAudioFile,
    };

    // ═══════════════════════════════════════════════════════════════
    // Tool-call parsing (text-based fallback)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Matches the <c>[TOOL_CALL:id]</c> marker emitted by the model.
    /// The JSON payload that follows is extracted separately via
    /// <see cref="ExtractJsonObject"/> to support nested objects
    /// (e.g. mk8.shell scripts).
    /// </summary>
    [GeneratedRegex(
        @"\[TOOL_CALL:(?<id>[^\]]+)\]\s*",
        RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex ToolCallMarkerPattern();

    /// <summary>
    /// Extracts a balanced JSON object starting at <paramref name="startIndex"/>.
    /// Returns <see langword="null"/> if the character at that position is not
    /// <c>{</c> or if the braces never balance.
    /// </summary>
    private static string? ExtractJsonObject(string text, int startIndex)
    {
        if (startIndex >= text.Length || text[startIndex] != '{')
            return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = startIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return text[startIndex..(i + 1)];
            }
        }

        return null;
    }

    private static IReadOnlyList<ParsedToolCall> ParseToolCalls(string content)
    {
        var markers = ToolCallMarkerPattern().Matches(content);
        if (markers.Count == 0)
            return [];

        var calls = new List<ParsedToolCall>(markers.Count);
        foreach (Match marker in markers)
        {
            var callId = marker.Groups["id"].Value;
            var jsonStart = marker.Index + marker.Length;
            var json = ExtractJsonObject(content, jsonStart);

            if (json is null)
                continue;

            try
            {
                var payload = JsonSerializer.Deserialize<ToolCallPayload>(json, JsonOptions);
                if (payload is not null)
                {
                    Guid? resourceId = Guid.TryParse(payload.ResourceId, out var rid) ? rid : null;
                    resourceId ??= Guid.TryParse(payload.TargetId, out var tid) ? tid : null;

                    // Default to mk8.shell for backwards compatibility
                    var actionType = AgentActionType.ExecuteAsSafeShell;

                    Guid? transcriptionModelId = Guid.TryParse(
                        payload.TranscriptionModelId, out var tmid) ? tmid : null;

                    DangerousShellType? dangerousShell = Enum.TryParse<DangerousShellType>(
                        payload.ShellType, ignoreCase: true, out var ds) ? ds : null;

                    if (dangerousShell.HasValue)
                        actionType = AgentActionType.UnsafeExecuteAsDangerousShell;
                    else if (transcriptionModelId.HasValue)
                        actionType = AgentActionType.TranscribeFromAudioDevice;

                    calls.Add(new ParsedToolCall(
                        callId,
                        actionType,
                        resourceId,
                        payload.SandboxId,
                        payload.Script is { } script
                            ? script.GetRawText()
                            : payload.Command,
                        dangerousShell,
                        actionType == AgentActionType.ExecuteAsSafeShell
                            ? SafeShellType.Mk8Shell : null,
                        transcriptionModelId,
                        payload.Language,
                        payload.WorkingDirectory));
                }
            }
            catch (JsonException)
            {
                // Malformed JSON — skip this call
            }
        }

        return calls;
    }

    private static string StripToolCallBlocks(string content)
    {
        var markers = ToolCallMarkerPattern().Matches(content);
        if (markers.Count == 0)
            return content;

        var sb = new StringBuilder(content);
        for (var i = markers.Count - 1; i >= 0; i--)
        {
            var marker = markers[i];
            var jsonStart = marker.Index + marker.Length;
            var json = ExtractJsonObject(content, jsonStart);
            var endIndex = json is not null
                ? jsonStart + json.Length
                : marker.Index + marker.Length;
            sb.Remove(marker.Index, endIndex - marker.Index);
        }

        return sb.ToString().Trim();
    }

    // ═══════════════════════════════════════════════════════════════
    // System prompt & tool definitions (loaded from embedded resources)
    // ═══════════════════════════════════════════════════════════════

    private static string BuildSystemPrompt(string? agentPrompt, bool nativeToolCalling)
    {
        var suffix = nativeToolCalling ? NativeToolSystemSuffix : ToolInstructions;

        if (string.IsNullOrEmpty(agentPrompt))
            return suffix;

        return agentPrompt + "\n\n" + suffix;
    }

    private static readonly string ToolInstructions =
        LoadEmbeddedResource("SharpClaw.Application.Core.tool-instructions-text.md");

    private static readonly string NativeToolSystemSuffix =
        LoadEmbeddedResource("SharpClaw.Application.Core.tool-instructions-native-suffix.md");

    private static readonly string Mk8ShellToolDescription =
        LoadEmbeddedResource("SharpClaw.Application.Core.tool-description-native.md");

    private static readonly IReadOnlyList<ChatToolDefinition> AllTools = BuildAllToolDefinitions();

    private static IReadOnlyList<ChatToolDefinition> BuildAllToolDefinitions()
    {
        var mk8Schema = BuildMk8ShellToolSchema();
        var resourceOnly = BuildResourceOnlySchema();
        var globalSchema = BuildGlobalActionSchema();
        var dangerousShellSchema = BuildDangerousShellSchema();
        var transcriptionSchema = BuildTranscriptionSchema();
        var createSubAgentSchema = BuildCreateSubAgentSchema();
        var createContainerSchema = BuildCreateContainerSchema();
        var manageAgentSchema = BuildManageAgentSchema();
        var editTaskSchema = BuildEditTaskSchema();
        var localhostBrowserSchema = BuildLocalhostBrowserSchema();
        var localhostCliSchema = BuildLocalhostCliSchema();

        return
        [
            // ── Shell execution ───────────────────────────────
            new("execute_mk8_shell", Mk8ShellToolDescription, mk8Schema),
            new("execute_dangerous_shell",
                "Execute a raw shell command via Bash, PowerShell, CommandPrompt, or Git. "
                + "Requires UnsafeExecuteAsDangerousShell permission. The command string is "
                + "passed directly to the interpreter with NO sandboxing. "
                + "Optional workingDirectory overrides the SystemUser's default CWD.",
                dangerousShellSchema),

            // ── Transcription ────────────────────────────────
            new("transcribe_from_audio_device",
                "Start live transcription from a system audio device. Requires a "
                + "transcription-capable model and an audio device resource.",
                transcriptionSchema),
            new("transcribe_from_audio_stream",
                "Transcribe an incoming audio stream. [NOT YET IMPLEMENTED — job will "
                + "execute but produce a stub result.]",
                transcriptionSchema),
            new("transcribe_from_audio_file",
                "Transcribe a pre-recorded audio file. [NOT YET IMPLEMENTED — job will "
                + "execute but produce a stub result.]",
                transcriptionSchema),

            // ── Global flags ─────────────────────────────────────
            new("create_sub_agent",
                "Create a new sub-agent under the calling agent. Provide a name, "
                + "modelId (GUID of the model to use), and optional systemPrompt. "
                + "Requires CreateSubAgent global permission.",
                createSubAgentSchema),
            new("create_container",
                "Create a new mk8.shell sandbox container. Provide a name (English "
                + "letters and digits only) and a path (parent directory where the "
                + "sandbox folder will be created). Requires CreateContainer global "
                + "permission.",
                createContainerSchema),
            new("register_info_store",
                "Register a new information store (local or external). [NOT YET "
                + "IMPLEMENTED — job will execute but produce a stub result.] Requires "
                + "RegisterInfoStore global permission.",
                globalSchema),
            new("access_localhost_in_browser",
                "Access a localhost URL through a headless browser (Chrome by "
                + "default). Set mode to 'html' (default) for the DOM content or "
                + "'screenshot' for a base64 PNG. Only localhost/127.0.0.1 URLs "
                + "are allowed. Requires AccessLocalhostInBrowser permission.",
                localhostBrowserSchema),
            new("access_localhost_cli",
                "Make a direct HTTP GET to a localhost URL and return the status "
                + "code, headers, and response body. No browser involved. Only "
                + "localhost/127.0.0.1 URLs are allowed. Requires "
                + "AccessLocalhostCli permission.",
                localhostCliSchema),

            // ── Per-resource ─────────────────────────────────────
            new("access_local_info_store",
                "Query or retrieve data from a local information store. [NOT YET "
                + "IMPLEMENTED — job will execute but produce a stub result.] "
                + "Requires AccessLocalInfoStore permission for the target resource.",
                resourceOnly),
            new("access_external_info_store",
                "Query or retrieve data from an external information store. [NOT YET "
                + "IMPLEMENTED — job will execute but produce a stub result.] "
                + "Requires AccessExternalInfoStore permission for the target resource.",
                resourceOnly),
            new("access_website",
                "Access or interact with a registered website resource. [NOT YET "
                + "IMPLEMENTED — job will execute but produce a stub result.] "
                + "Requires AccessWebsite permission for the target resource.",
                resourceOnly),
            new("query_search_engine",
                "Query a registered search engine resource. [NOT YET IMPLEMENTED — job "
                + "will execute but produce a stub result.] Requires QuerySearchEngine "
                + "permission for the target resource.",
                resourceOnly),
            new("access_container",
                "Access a container resource (read metadata, inspect). [NOT YET "
                + "IMPLEMENTED — job will execute but produce a stub result.] "
                + "Requires AccessContainer permission for the target resource.",
                resourceOnly),
            new("manage_agent",
                "Update another agent's name, system prompt, or model. Provide "
                + "targetId (the agent GUID) and the fields to update. "
                + "Requires ManageAgent permission for the target.",
                manageAgentSchema),
            new("edit_task",
                "Edit a specific scheduled task's name, interval, or retry "
                + "settings. Provide targetId (the task GUID) and the fields "
                + "to update. Requires EditTask permission.",
                editTaskSchema),
            new("access_skill",
                "Retrieve the instruction text of a registered skill. Returns the "
                + "full SkillText so the agent can learn how to use the associated "
                + "resource. Requires AccessSkill permission for the target skill.",
                resourceOnly),
        ];
    }

    private static JsonElement BuildMk8ShellToolSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resourceId": {
                        "type": "string",
                        "description": "The GUID of the container resource to execute against."
                    },
                    "sandboxId": {
                        "type": "string",
                        "description": "The mk8.shell sandbox name (resolved from the local registry)."
                    },
                    "script": {
                        "type": "object",
                        "description": "The mk8.shell script object.",
                        "properties": {
                            "operations": {
                                "type": "array",
                                "description": "Ordered list of operations. Each needs verb and args.",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "verb": { "type": "string" },
                                        "args": { "type": "array", "items": { "type": "string" } },
                                        "workingDirectory": {
                                            "type": "string",
                                            "description": "Optional per-step working directory override (e.g. '$WORKSPACE/bananaapp'). ProcRun processes spawn with this as their CWD instead of the sandbox root. Use this instead of flags like git -C which are not in the template whitelist."
                                        }
                                    },
                                    "required": ["verb", "args"]
                                }
                            },
                            "options": { "type": "object" },
                            "cleanup": {
                                "type": "array",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "verb": { "type": "string" },
                                        "args": { "type": "array", "items": { "type": "string" } },
                                        "workingDirectory": {
                                            "type": "string",
                                            "description": "Optional per-step working directory override."
                                        }
                                    },
                                    "required": ["verb", "args"]
                                }
                            }
                        },
                        "required": ["operations"]
                    }
                },
                "required": ["resourceId", "sandboxId", "script"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildResourceOnlySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "The GUID of the target resource."
                    }
                },
                "required": ["targetId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildGlobalActionSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {},
                "additionalProperties": false
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildDangerousShellSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resourceId": {
                        "type": "string",
                        "description": "The GUID of the SystemUser resource to execute as."
                    },
                    "shellType": {
                        "type": "string",
                        "enum": ["Bash", "PowerShell", "CommandPrompt", "Git"],
                        "description": "The shell interpreter to use."
                    },
                    "command": {
                        "type": "string",
                        "description": "The raw command string to pass to the interpreter."
                    },
                    "workingDirectory": {
                        "type": "string",
                        "description": "Absolute path where the shell process should be spawned. Overrides the SystemUser's default working directory when set."
                    }
                },
                "required": ["resourceId", "shellType", "command"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildTranscriptionSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "The GUID of the audio device resource."
                    },
                    "transcriptionModelId": {
                        "type": "string",
                        "description": "The GUID of the transcription-capable model to use."
                    },
                    "language": {
                        "type": "string",
                        "description": "Optional BCP-47 language code (e.g. 'en', 'de')."
                    }
                },
                "required": ["targetId", "transcriptionModelId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildCreateSubAgentSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {
                        "type": "string",
                        "description": "Name for the new sub-agent."
                    },
                    "modelId": {
                        "type": "string",
                        "description": "The GUID of the model the sub-agent should use."
                    },
                    "systemPrompt": {
                        "type": "string",
                        "description": "Optional system prompt for the sub-agent."
                    }
                },
                "required": ["name", "modelId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildCreateContainerSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {
                        "type": "string",
                        "description": "Sandbox name (English letters and digits only)."
                    },
                    "path": {
                        "type": "string",
                        "description": "Absolute parent directory where the sandbox folder will be created."
                    },
                    "description": {
                        "type": "string",
                        "description": "Optional description for the container."
                    }
                },
                "required": ["name", "path"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildManageAgentSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "The GUID of the agent to manage."
                    },
                    "name": {
                        "type": "string",
                        "description": "New name for the agent."
                    },
                    "systemPrompt": {
                        "type": "string",
                        "description": "New system prompt for the agent."
                    },
                    "modelId": {
                        "type": "string",
                        "description": "GUID of the new model to assign."
                    }
                },
                "required": ["targetId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildEditTaskSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "The GUID of the scheduled task to edit."
                    },
                    "name": {
                        "type": "string",
                        "description": "New name for the task."
                    },
                    "repeatIntervalMinutes": {
                        "type": "integer",
                        "description": "New repeat interval in minutes. 0 to remove."
                    },
                    "maxRetries": {
                        "type": "integer",
                        "description": "New maximum retry count."
                    }
                },
                "required": ["targetId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildLocalhostBrowserSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "url": {
                        "type": "string",
                        "description": "The localhost URL to access (e.g. 'http://localhost:5000/api/health'). Only localhost/127.0.0.1/[::1] allowed."
                    },
                    "mode": {
                        "type": "string",
                        "enum": ["html", "screenshot"],
                        "description": "Return mode. 'html' (default) returns the DOM content. 'screenshot' returns a base64-encoded PNG."
                    }
                },
                "required": ["url"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildLocalhostCliSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "url": {
                        "type": "string",
                        "description": "The localhost URL to GET (e.g. 'http://localhost:5000/api/health'). Only localhost/127.0.0.1/[::1] allowed."
                    }
                },
                "required": ["url"]
            }
            """);
        return doc.RootElement.Clone();
    }

    // ═══════════════════════════════════════════════════════════════
    // Embedded resource loader
    // ═══════════════════════════════════════════════════════════════

    private static string LoadEmbeddedResource(string name)
    {
        using var stream = typeof(ChatService).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}' not found in {typeof(ChatService).Assembly.FullName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal types
    // ═══════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ParsedToolCall(
        string CallId,
        AgentActionType ActionType,
        Guid? ResourceId,
        string? SandboxId,
        string? ScriptJson,
        DangerousShellType? DangerousShellType = null,
        SafeShellType? SafeShellType = null,
        Guid? TranscriptionModelId = null,
        string? Language = null,
        string? WorkingDirectory = null);

    private sealed class ToolCallPayload
    {
        public string? ResourceId { get; set; }
        public string? SandboxId { get; set; }
        public JsonElement? Script { get; set; }
        public string? Command { get; set; }
        public string? ShellType { get; set; }
        public string? TranscriptionModelId { get; set; }
        public string? Language { get; set; }
        public string? TargetId { get; set; }
        public string? Query { get; set; }
        public string? Url { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? Name { get; set; }
        public string? ModelId { get; set; }
        public string? SystemPrompt { get; set; }
        public string? Path { get; set; }
        public string? Description { get; set; }
        public int? RepeatIntervalMinutes { get; set; }
        public int? MaxRetries { get; set; }
    }

    private readonly record struct ToolLoopResult(
        string AssistantContent,
        List<AgentJobResponse> JobResults);
}
