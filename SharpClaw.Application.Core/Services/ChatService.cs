using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts.Modules;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed class ChatService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    ColdEntityStore coldStore,
    ProviderApiClientFactory clientFactory,
    IHttpClientFactory httpClientFactory,
    AgentJobService jobService,
    LocalModelService localModelService,
    HeaderTagProcessor headerTagProcessor,
    ThreadActivitySignal threadActivity,
    ModuleRegistry moduleRegistry,
    ModuleMetricsCollector metricsCollector,
    IServiceScopeFactory serviceScopeFactory,
    IServiceProvider serviceProvider,
    IConfiguration configuration)
{
    private const int MaxHistoryMessages = 50;
    private const int MaxHistoryCharacters = 100_000;

    /// <summary>
    /// Maximum number of tool-call round-trips before forcing a final
    /// response.  Prevents infinite loops when the model keeps emitting
    /// tool calls.
    /// </summary>
    private const int MaxToolCallRounds = 10;

    private readonly bool _disableCustomProviderParameters =
        configuration.GetValue<bool>("Agent:DisableCustomProviderParameters");

    /// <summary>
    /// Sends a chat message through the specified channel, optionally
    /// within a thread, executing tool calls as needed.
    /// </summary>
    public async Task<ChatResponse> SendMessageAsync(
        Guid channelId, ChatRequest request,
        Guid? threadId = null,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback = null,
        CancellationToken ct = default)
    {
        bool userMessagePersisted = false;

        var channel = await db.Channels
            .Include(c => c.Agent!).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent!).ThenInclude(a => a.ToolAwarenessSet)
            .Include(c => c.ToolAwarenessSet)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.ToolAwarenessSet)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct)
            ?? throw new ArgumentException($"Channel {channelId} not found.");

        var agent = ResolveAgent(channel, request.AgentId);
        var model = agent.Model
            ?? throw new InvalidOperationException(
                $"Agent '{agent.Name}' ({agent.Id}) has no model assigned. " +
                "Assign a valid model before using this agent for chat.");
        var provider = model.Provider
            ?? throw new InvalidOperationException(
                $"Model '{model.Name}' ({model.Id}) has no provider assigned.");

        var isLocal = provider.ProviderType == ProviderType.LlamaSharp;
        if (!isLocal && string.IsNullOrEmpty(provider.EncryptedApiKey))
            throw new InvalidOperationException("Provider does not have an API key configured.");

        // Auto-load local model if not already loaded
        if (isLocal)
            await localModelService.EnsureReadyForChatAsync(model.Id, ct);

        // Acquire per-thread lock for sequential processing
        IDisposable? threadLock = null;
        if (threadId is not null)
        {
            threadLock = await threadActivity.AcquireThreadLockAsync(threadId.Value, ct);
            threadActivity.Publish(threadId.Value,
                new ThreadActivityEvent(ThreadActivityEventType.Processing, request.ClientType));
        }

        try
        {

        // Build history: only when a thread is specified; otherwise a single one-shot.
        List<ChatCompletionMessage> history;
        if (threadId is not null)
        {
            history = await LoadThreadHistoryAsync(threadId.Value, ct);
        }
        else
        {
            history = [];
        }

        history.Add(new ChatCompletionMessage("user", request.Message));

        var apiKey = isLocal ? "local" : ApiKeyEncryptor.DecryptOrPassthrough(provider.EncryptedApiKey!, encryptionOptions.Key);
        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);
        if (client is LocalInferenceApiClient lic)
            lic.CurrentModelId = model.Id;
        var useNativeTools = client.SupportsNativeToolCalling;
        var disableTools = channel.DisableToolSchemas || agent.DisableToolSchemas;
        var enableTools = !disableTools && useNativeTools;
        var systemPrompt = enableTools
            ? BuildSystemPrompt(agent.SystemPrompt)
            : agent.SystemPrompt ?? "";

        // Build chat header for the user message (if enabled)
        var chatHeader = await BuildChatHeaderAsync(channel, agent, request.ClientType, request.EditorContext, ct,
            taskContext: request.TaskContext, externalUsername: request.ExternalUsername, externalDisplayName: request.ExternalDisplayName);
        var messageForModel = chatHeader is not null
            ? chatHeader + request.Message
            : request.Message;

        // Replace last history entry with the header-prefixed version for model
        history[^1] = new ChatCompletionMessage("user", messageForModel);

        using var httpClient = httpClientFactory.CreateClient();

        var modelCapabilities = model.Capabilities;
        var maxTokens = agent.MaxCompletionTokens;
        var providerParams = _disableCustomProviderParameters ? null : agent.ProviderParameters;
        var completionParams = BuildCompletionParameters(agent);
        CompletionParameterValidator.ValidateOrThrow(completionParams, provider.ProviderType);
        var toolAwareness = enableTools ? (channel.ToolAwarenessSet?.Tools ?? agent.ToolAwarenessSet?.Tools) : null;

        // Persist user message immediately so it gets an accurate
        // CreatedAt and survives crashes during LLM inference.
        var senderUserId = jobService.GetSessionUserId();
        var senderUsername = senderUserId.HasValue
            ? (await db.Users.Where(u => u.Id == senderUserId.Value).Select(u => u.Username).FirstOrDefaultAsync(ct))
            : request.ExternalDisplayName ?? request.ExternalUsername;

        var userMessage = new ChatMessageDB
        {
            Role = "user",
            Content = request.Message,
            ChannelId = channelId,
            ThreadId = threadId,
            SenderUserId = senderUserId,
            SenderUsername = senderUsername,
            ClientType = request.ClientType
        };

        db.ChatMessages.Add(userMessage);
        await db.SaveChangesAsync(ct);
        userMessagePersisted = true;

        var loopResult = enableTools
            ? await RunNativeToolLoopAsync(
                client, httpClient, apiKey, model.Name, systemPrompt,
                history, agent.Id, channelId, modelCapabilities, maxTokens, providerParams, completionParams, approvalCallback, ct,
                taskContext: request.TaskContext, toolAwareness: toolAwareness, threadId: threadId)
            : await RunPlainCompletionAsync(
                client, httpClient, apiKey, model.Name, systemPrompt,
                history, maxTokens, providerParams, completionParams, ct);

        // Persist assistant message after LLM completes
        var assistantMessage = new ChatMessageDB
        {
            Role = "assistant",
            Content = loopResult.AssistantContent,
            ChannelId = channelId,
            ThreadId = threadId,
            SenderAgentId = agent.Id,
            SenderAgentName = agent.Name,
            ClientType = request.ClientType,
            PromptTokens = loopResult.TotalPromptTokens > 0 ? loopResult.TotalPromptTokens : null,
            CompletionTokens = loopResult.TotalCompletionTokens > 0 ? loopResult.TotalCompletionTokens : null
        };

        db.ChatMessages.Add(assistantMessage);
        await db.SaveChangesAsync(ct);

        if (threadId is not null)
            threadActivity.Publish(threadId.Value,
                new ThreadActivityEvent(ThreadActivityEventType.NewMessages, request.ClientType));

        // Piggyback cost data on the response so callers don't need
        // a separate round-trip to the /cost endpoints.
        var channelCost = await GetChannelCostAsync(channelId, ct);
        var threadCost = threadId is not null
            ? await GetThreadCostAsync(channelId, threadId.Value, ct)
            : null;
        var agentCost = await GetAgentCostAsync(agent.Id, ct);

        return new ChatResponse(
            ToMessageResponse(userMessage),
            ToMessageResponse(assistantMessage),
            loopResult.JobResults.Count > 0 ? loopResult.JobResults : null,
            channelCost,
            threadCost,
            agentCost);

        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await PersistStreamErrorAsync(channelId, threadId, request, ex,
                userMessagePersisted, ct);
            throw;
        }
        finally
        {
            threadLock?.Dispose();
            if (isLocal)
                localModelService.ReleaseAfterChat(model.Id);
        }
    }

    public async Task<IReadOnlyList<ChatMessageResponse>> GetHistoryAsync(
        Guid channelId, Guid? threadId = null, int limit = 50, CancellationToken ct = default)
    {
        var indexFilter = threadId is not null
            ? new ColdEntityStore.IndexFilter("ThreadId", threadId.Value)
            : new ColdEntityStore.IndexFilter("ChannelId", channelId);
        var messages = await coldStore.QueryAsync<ChatMessageDB>(
            m => threadId is not null ? m.ThreadId == threadId : m.ChannelId == channelId,
            limit,
            ct,
            indexFilter);

        return messages
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new ChatMessageResponse(
                m.Role, m.Content, m.CreatedAt,
                m.SenderUserId, m.SenderUsername,
                m.SenderAgentId, m.SenderAgentName,
                m.ClientType != null ? m.ClientType.ToString() : null))
            .ToList();
    }

    private static ChatMessageResponse ToMessageResponse(ChatMessageDB m) =>
        new(m.Role, m.Content, m.CreatedAt,
            m.SenderUserId, m.SenderUsername,
            m.SenderAgentId, m.SenderAgentName,
            m.ClientType?.ToString());

    // ═══════════════════════════════════════════════════════════════
    // Token cost aggregation
    // ═══════════════════════════════════════════════════════════════

    public async Task<ChannelCostResponse> GetChannelCostAsync(
        Guid channelId, CancellationToken ct = default)
    {
        var messages = await coldStore.QueryAllAsync<ChatMessageDB>(
            m => m.ChannelId == channelId && m.PromptTokens != null, ct,
            new ColdEntityStore.IndexFilter("ChannelId", channelId));

        var breakdown = messages
            .GroupBy(m => new { m.SenderAgentId, m.SenderAgentName })
            .Where(g => g.Key.SenderAgentId.HasValue)
            .Select(g => new AgentTokenBreakdown(
                g.Key.SenderAgentId!.Value,
                g.Key.SenderAgentName ?? "Unknown",
                g.Sum(m => m.PromptTokens!.Value),
                g.Sum(m => m.CompletionTokens ?? 0),
                g.Sum(m => m.PromptTokens!.Value) + g.Sum(m => m.CompletionTokens ?? 0)))
            .OrderByDescending(b => b.TotalTokens)
            .ToList();

        var totalPrompt = breakdown.Sum(b => b.PromptTokens);
        var totalCompletion = breakdown.Sum(b => b.CompletionTokens);

        return new ChannelCostResponse(
            channelId, totalPrompt, totalCompletion,
            totalPrompt + totalCompletion, breakdown);
    }

    public async Task<ThreadCostResponse?> GetThreadCostAsync(
        Guid channelId, Guid threadId, CancellationToken ct = default)
    {
        var threadExists = await db.ChatThreads
            .AnyAsync(t => t.Id == threadId && t.ChannelId == channelId, ct);
        if (!threadExists) return null;

        var rows = await coldStore.QueryAllAsync<ChatMessageDB>(
            m => m.ThreadId == threadId && m.PromptTokens != null, ct,
            new ColdEntityStore.IndexFilter("ThreadId", threadId));

        var breakdown = rows
            .GroupBy(m => new { m.SenderAgentId, m.SenderAgentName })
            .Where(g => g.Key.SenderAgentId.HasValue)
            .Select(g => new AgentTokenBreakdown(
                g.Key.SenderAgentId!.Value,
                g.Key.SenderAgentName ?? "Unknown",
                g.Sum(m => m.PromptTokens!.Value),
                g.Sum(m => m.CompletionTokens ?? 0),
                g.Sum(m => m.PromptTokens!.Value) + g.Sum(m => m.CompletionTokens ?? 0)))
            .OrderByDescending(b => b.TotalTokens)
            .ToList();

        var totalPrompt = breakdown.Sum(b => b.PromptTokens);
        var totalCompletion = breakdown.Sum(b => b.CompletionTokens);

        return new ThreadCostResponse(
            threadId, channelId, totalPrompt, totalCompletion,
            totalPrompt + totalCompletion, breakdown);
    }

    /// <summary>
    /// Aggregated token usage for a single agent across all channels,
    /// with per-channel breakdown.
    /// </summary>
    public async Task<AgentCostResponse?> GetAgentCostAsync(
        Guid agentId, CancellationToken ct = default)
    {
        var agent = await db.Agents.FindAsync([agentId], ct);
        if (agent is null) return null;

        var messages = await coldStore.QueryAllAsync<ChatMessageDB>(
            m => m.SenderAgentId == agentId && m.PromptTokens != null, ct,
            new ColdEntityStore.IndexFilter("SenderAgentId", agentId));

        var channelBreakdown = messages
            .GroupBy(m => m.ChannelId)
            .Select(g => new AgentChannelTokenBreakdown(
                g.Key,
                g.Sum(m => m.PromptTokens!.Value),
                g.Sum(m => m.CompletionTokens ?? 0),
                g.Sum(m => m.PromptTokens!.Value) + g.Sum(m => m.CompletionTokens ?? 0)))
            .OrderByDescending(b => b.TotalTokens)
            .ToList();

        var totalPrompt = channelBreakdown.Sum(b => b.PromptTokens);
        var totalCompletion = channelBreakdown.Sum(b => b.CompletionTokens);

        return new AgentCostResponse(
            agentId, agent.Name,
            totalPrompt, totalCompletion,
            totalPrompt + totalCompletion, channelBreakdown);
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
    // Thread history loading
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads messages for a thread, respecting the thread's per-thread
    /// <see cref="ChatThreadDB.MaxMessages"/> and
    /// <see cref="ChatThreadDB.MaxCharacters"/> limits.
    /// Falls back to system defaults (<see cref="MaxHistoryMessages"/>
    /// and <see cref="MaxHistoryCharacters"/>).
    /// When both limits are set, only messages fitting within both are
    /// returned.
    /// </summary>
    private async Task<List<ChatCompletionMessage>> LoadThreadHistoryAsync(
        Guid threadId, CancellationToken ct)
    {
        var thread = await db.ChatThreads.FindAsync([threadId], ct);
        var maxMessages = thread?.MaxMessages ?? MaxHistoryMessages;
        var maxChars = thread?.MaxCharacters ?? MaxHistoryCharacters;

        var cold = await coldStore.QueryAsync<ChatMessageDB>(
            m => m.ThreadId == threadId,
            maxMessages,
            ct,
            new ColdEntityStore.IndexFilter("ThreadId", threadId));

        var messages = cold
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatCompletionMessage(m.Role, m.Content))
            .ToList();

        // Trim oldest messages until the total character count fits.
        var totalChars = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
            totalChars += messages[i].Content.Length;

        while (messages.Count > 0 && totalChars > maxChars)
        {
            totalChars -= messages[0].Content.Length;
            messages.RemoveAt(0);
        }

        return messages;
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
        ChannelDB channel, AgentDB agent, ChatClientType clientType,
        EditorContext? editorContext, CancellationToken ct,
        TaskChatContext? taskContext = null,
        string? externalUsername = null, string? externalDisplayName = null)
    {
        // Channel-level flag takes precedence; fall back to context.
        var disabled = channel.DisableChatHeader
            || (channel.AgentContext?.DisableChatHeader ?? false);

        if (disabled)
            return null;

        // ── Task-sourced message: lightweight header, no user lookup ──
        if (taskContext is not null)
        {
            var taskSb = new StringBuilder();
            taskSb.Append("[time: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
            taskSb.Append(" | source: automated task");
            taskSb.Append(" | task: ").Append(taskContext.TaskName);

            var store = TaskSharedData.Get(taskContext.InstanceId);
            if (store is not null)
            {
                var lightText = store.LightData;
                if (lightText is not null)
                    taskSb.Append(" | shared-data: ").Append(lightText);

                var bigEntries = store.ListBig();
                if (bigEntries.Count > 0)
                {
                    taskSb.Append(" | big-data-ids: [");
                    taskSb.Append(string.Join(", ", bigEntries.Select(e => $"{e.Id}:\"{e.Title}\"")));
                    taskSb.Append(']');
                }
            }

            await AppendAgentSuffixAsync(taskSb, agent.Id, channel.Id, editorContext: null, ct);
            return taskSb.ToString();
        }

        // ── Custom header resolution: channel overrides agent ────────
        var customTemplate = channel.CustomChatHeader ?? agent.CustomChatHeader;
        if (customTemplate is not null)
        {
            var userId2 = jobService.GetSessionUserId();
            return await headerTagProcessor.ExpandAsync(
                customTemplate, channel, agent, clientType, editorContext, userId2, ct);
        }

        var userId = jobService.GetSessionUserId();

        // ── External user (bot-forwarded message): no DB session ─────
        if (userId is null && externalUsername is not null)
        {
            var extSb = new StringBuilder();
            extSb.Append("[time: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
            extSb.Append(" | user: ").Append(externalDisplayName ?? externalUsername);
            if (externalDisplayName is not null && externalUsername != externalDisplayName)
                extSb.Append(" (@").Append(externalUsername).Append(')');
            extSb.Append(" | via: ").Append(clientType);

            await AppendAgentSuffixAsync(extSb, agent.Id, channel.Id, editorContext: null, ct);
            return extSb.ToString();
        }

        if (userId is null)
            return null;

        var user = await db.Users
            .Include(u => u.Role)
            .ThenInclude(r => r!.PermissionSet)
            .AsSplitQuery()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return null;

        // Load resource grants if the user has a permission set
        PermissionSetDB? ps = null;
        if (user.Role?.PermissionSetId is { } psId)
        {
            ps = await db.PermissionSets
                .Include(p => p.GlobalFlags)
                .Include(p => p.ResourceAccesses)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == psId, ct);
        }

        var sb = new StringBuilder();
        sb.Append("[time: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        sb.Append(" | user: ").Append(user.Username);
        sb.Append(" | via: ").Append(clientType);

        if (user.Role is not null && ps is not null)
        {
            var grants = await CollectGrantsWithResourcesAsync(ps, ct);

            if (grants.Count > 0)
                sb.Append(" | role: ").Append(user.Role.Name)
                  .Append(" (").Append(string.Join(", ", grants)).Append(')');
            else
                sb.Append(" | role: ").Append(user.Role.Name);
        }

        if (!string.IsNullOrWhiteSpace(user.Bio))
            sb.Append(" | bio: ").Append(user.Bio);

        await AppendAgentSuffixAsync(sb, agent.Id, channel.Id, editorContext, ct);
        return sb.ToString();
    }

    /// <summary>
    /// Appends the shared agent-role, policy, accessible-threads, optional
    /// editor context, and closing bracket to a header being constructed.
    /// Shared across all header paths (authenticated user, external user, task).
    /// </summary>
    private async Task AppendAgentSuffixAsync(
        StringBuilder sb, Guid agentId, Guid channelId,
        EditorContext? editorContext, CancellationToken ct)
    {
        var agentWithRole = await db.Agents
            .Include(a => a.Role)
            .ThenInclude(r => r!.PermissionSet)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        if (agentWithRole?.Role is { } agentRole)
        {
            PermissionSetDB? agentPs = null;
            if (agentRole.PermissionSetId is { } agentPsId)
            {
                agentPs = await db.PermissionSets
                        .Include(p => p.GlobalFlags)
                        .Include(p => p.ResourceAccesses)
                        .AsSplitQuery()
                        .FirstOrDefaultAsync(p => p.Id == agentPsId, ct);
            }

            sb.Append(" | agent-role: ").Append(agentRole.Name);
            if (agentPs is not null)
            {
                var agentGrants = await CollectGrantsWithResourcesAsync(agentPs, ct);
                if (agentGrants.Count > 0)
                    sb.Append(" (").Append(string.Join(", ", agentGrants)).Append(')');
            }
        }
        else
        {
            sb.Append(" | agent-role: (none)");
        }

        sb.Append(" | policy: unlisted-resource/GUID=denied; disclose gaps to user");

        var accessibleThreads = await GetAccessibleThreadsAsync(agentId, channelId, ct);
        if (accessibleThreads.Count > 0)
        {
            sb.Append(" | accessible-threads: ");
            sb.Append(string.Join(", ", accessibleThreads.Select(
                t => $"{t.ThreadName} [{t.ChannelTitle}] ({t.ThreadId:D})")));
        }

        if (editorContext is not null)
        {
            sb.Append(" | editor: ").Append(editorContext.EditorType);
            if (editorContext.EditorVersion is not null)
                sb.Append(' ').Append(editorContext.EditorVersion);
            if (editorContext.WorkspacePath is not null)
                sb.Append(" workspace=").Append(editorContext.WorkspacePath);
            if (editorContext.ActiveFilePath is not null)
                sb.Append(" file=").Append(editorContext.ActiveFilePath);
            if (editorContext.SelectedText is { Length: > 0 and <= 200 })
                sb.Append(" selection=\"").Append(editorContext.SelectedText).Append('"');
        }

        sb.AppendLine("]");
    }

    /// <summary>
    /// Finds threads on other channels that the agent can read via
    /// cross-thread history.  Used by the chat header to populate the
    /// <c>accessible-threads</c> section.
    /// </summary>
    private async Task<List<(Guid ThreadId, string ThreadName, Guid ChannelId, string ChannelTitle)>>
        GetAccessibleThreadsAsync(Guid agentId, Guid currentChannelId, CancellationToken ct)
    {
        var agentWithRole = await db.Agents
            .Include(a => a.Role)
                .ThenInclude(r => r!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        var agentPs = agentWithRole?.Role?.PermissionSet;
        if (agentPs is null || !agentPs.GlobalFlags.Any(f => f.FlagKey == "CanReadCrossThreadHistory"))
            return [];

        var isIndependent = (agentPs.GlobalFlags
            .FirstOrDefault(f => f.FlagKey == "CanReadCrossThreadHistory")
            ?.Clearance ?? PermissionClearance.Unset) == PermissionClearance.Independent;

        var channels = await db.Channels
            .Include(c => c.AllowedAgents)
            .Include(c => c.PermissionSet)
                .ThenInclude(ps => ps!.GlobalFlags)
            .Include(c => c.AgentContext)
                .ThenInclude(ctx => ctx!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .Where(c => c.Id != currentChannelId)
            .Where(c => c.AgentId == agentId || c.AllowedAgents.Any(a => a.Id == agentId))
            .ToListAsync(ct);

        if (!isIndependent)
        {
            channels = channels
                .Where(c =>
                {
                    var effectivePs = c.PermissionSet ?? c.AgentContext?.PermissionSet;
                    return effectivePs?.GlobalFlags.Any(f => f.FlagKey == "CanReadCrossThreadHistory") == true;
                })
                .ToList();
        }

        if (channels.Count == 0)
            return [];

        var channelIds = channels.Select(c => c.Id).ToList();
        var threads = await db.ChatThreads
            .Where(t => channelIds.Contains(t.ChannelId))
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new { t.Id, t.Name, t.ChannelId })
            .ToListAsync(ct);

        var channelTitles = channels.ToDictionary(c => c.Id, c => c.Title);

        return threads
            .Select(t => (t.Id, t.Name, t.ChannelId, channelTitles[t.ChannelId]))
            .ToList();
    }

    /// <summary>
    /// Collects grant names with enumerated resource IDs for the chat
    /// header (both user and agent sections). When a wildcard grant
    /// (<see cref="WellKnownIds.AllResources"/>) is present, all resource
    /// IDs of that type are resolved from the database so the reader
    /// knows exactly which resources the permission set covers.
    /// </summary>
    /// <summary>
    /// Collects grant names with enumerated resource IDs for the chat
    /// header (both user and agent sections). Uses the unified
    /// <see cref="ResourceAccessDB"/> collection and the same expander
    /// table as <see cref="HeaderTagProcessor"/>.
    /// </summary>
    private async Task<List<string>> CollectGrantsWithResourcesAsync(
        PermissionSetDB ps, CancellationToken ct)
    {
        var grants = new List<string>();

        // Global flags — generic iteration over the GlobalFlags collection.
        foreach (var flag in ps.GlobalFlags)
            grants.Add(flag.FlagKey.StartsWith("Can", StringComparison.Ordinal)
                ? flag.FlagKey[3..]
                : flag.FlagKey);

        foreach (var desc in moduleRegistry.GetAllResourceTypeDescriptors())
        {
            var grantedIds = ps.ResourceAccesses
                .Where(a => a.ResourceType == desc.ResourceType)
                .Select(a => a.ResourceId)
                .ToList();

            await AppendResourceGrantAsync(grants, desc.GrantLabel, grantedIds,
                () => desc.LoadAllIds(serviceProvider, ct));
        }

        return grants;
    }

    /// <summary>
    /// Appends a grant entry with resource IDs. If any grant entry
    /// matches <see cref="WellKnownIds.AllResources"/>, all IDs of that
    /// type are loaded from the database so the agent sees the resolved
    /// list instead of the wildcard.
    /// </summary>
    private static async Task AppendResourceGrantAsync(
        List<string> grants, string grantName,
        IEnumerable<Guid> grantedIds, Func<Task<List<Guid>>> loadAllIdsAsync)
    {
        var ids = grantedIds.ToList();
        if (ids.Count == 0)
            return;

        List<Guid> resolved;
        if (ids.Any(id => id == WellKnownIds.AllResources))
            resolved = await loadAllIdsAsync();
        else
            resolved = ids;

        if (resolved.Count == 0)
        {
            grants.Add(grantName);
            return;
        }

        var idList = string.Join(",", resolved.Select(id => id.ToString("D")));
        grants.Add($"{grantName}[{idList}]");
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
        Guid? threadId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = await db.Channels
            .Include(c => c.Agent!).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent!).ThenInclude(a => a.ToolAwarenessSet)
            .Include(c => c.ToolAwarenessSet)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.ToolAwarenessSet)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct)
            ?? throw new ArgumentException($"Channel {channelId} not found.");

        var agent = ResolveAgent(channel, request.AgentId);
        var model = agent.Model
            ?? throw new InvalidOperationException(
                $"Agent '{agent.Name}' ({agent.Id}) has no model assigned. " +
                "Assign a valid model before using this agent for chat.");
        var provider = model.Provider
            ?? throw new InvalidOperationException(
                $"Model '{model.Name}' ({model.Id}) has no provider assigned.");

        var isLocal = provider.ProviderType == ProviderType.LlamaSharp;
        if (!isLocal && string.IsNullOrEmpty(provider.EncryptedApiKey))
            throw new InvalidOperationException("Provider does not have an API key configured.");

        // Auto-load local model if not already loaded
        if (isLocal)
            await localModelService.EnsureReadyForChatAsync(model.Id, ct);

        // Acquire per-thread lock for sequential processing
        IDisposable? threadLock = null;
        if (threadId is not null)
        {
            threadLock = await threadActivity.AcquireThreadLockAsync(threadId.Value, ct);
            threadActivity.Publish(threadId.Value,
                new ThreadActivityEvent(ThreadActivityEventType.Processing, request.ClientType));
        }

        try
        {

        // Build history: only when a thread is specified; otherwise a single one-shot.
        List<ChatCompletionMessage> history;
        if (threadId is not null)
        {
            history = await LoadThreadHistoryAsync(threadId.Value, ct);
        }
        else
        {
            history = [];
        }

        history.Add(new ChatCompletionMessage("user", request.Message));

        var apiKey = isLocal ? "local" : ApiKeyEncryptor.DecryptOrPassthrough(provider.EncryptedApiKey!, encryptionOptions.Key);
        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);
        if (client is LocalInferenceApiClient streamLic)
            streamLic.CurrentModelId = model.Id;
        var systemPrompt = channel.DisableToolSchemas || agent.DisableToolSchemas
            ? agent.SystemPrompt ?? ""
            : BuildSystemPrompt(agent.SystemPrompt);

        // Build chat header for the user message (if enabled)
        var chatHeader = await BuildChatHeaderAsync(channel, agent, request.ClientType, request.EditorContext, ct,
            taskContext: request.TaskContext, externalUsername: request.ExternalUsername, externalDisplayName: request.ExternalDisplayName);
        if (chatHeader is not null)
            history[^1] = new ChatCompletionMessage("user", chatHeader + request.Message);

        using var httpClient = httpClientFactory.CreateClient();

        var supportsVision = model.Capabilities.HasFlag(ModelCapability.Vision);
        var maxTokens = agent.MaxCompletionTokens;
        var providerParams = _disableCustomProviderParameters ? null : agent.ProviderParameters;
        var completionParams = BuildCompletionParameters(agent);
        CompletionParameterValidator.ValidateOrThrow(completionParams, provider.ProviderType);
        var toolAwareness = channel.DisableToolSchemas || agent.DisableToolSchemas
            ? null
            : (channel.ToolAwarenessSet?.Tools ?? agent.ToolAwarenessSet?.Tools);
        var effectiveTools = channel.DisableToolSchemas || agent.DisableToolSchemas
            ? []
            : GetEffectiveTools(request.TaskContext, toolAwareness);

        // Persist user message immediately so it gets an accurate
        // CreatedAt and survives crashes during LLM inference.
        var senderUserId = jobService.GetSessionUserId();
        var senderUsername = senderUserId.HasValue
            ? (await db.Users.Where(u => u.Id == senderUserId.Value).Select(u => u.Username).FirstOrDefaultAsync(ct))
            : request.ExternalDisplayName ?? request.ExternalUsername;

        var userMessage = new ChatMessageDB
        {
            Role = "user",
            Content = request.Message,
            ChannelId = channelId,
            ThreadId = threadId,
            SenderUserId = senderUserId,
            SenderUsername = senderUsername,
            ClientType = request.ClientType
        };

        db.ChatMessages.Add(userMessage);
        await db.SaveChangesAsync(ct);

        // Convert history to tool-aware messages
        var messages = new List<ToolAwareMessage>(history.Count);
        foreach (var msg in history)
            messages.Add(new ToolAwareMessage { Role = msg.Role, Content = msg.Content });

        var jobResults = new List<AgentJobResponse>();
        var fullContent = new StringBuilder();
        var rounds = 0;
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var roundJobIds = new List<Guid>();

        while (true)
        {
            // Stream the current round
            ChatCompletionResult? roundResult = null;

            await foreach (var chunk in client.StreamChatCompletionWithToolsAsync(
                httpClient, apiKey, model.Name, systemPrompt, messages, effectiveTools, maxTokens, providerParams, completionParams, ct))
            {
                if (chunk.Delta is not null)
                    yield return ChatStreamEvent.TextDelta(chunk.Delta);

                if (chunk.IsFinished)
                    roundResult = chunk.Finished;
            }

            if (roundResult is null)
                break;

            if (roundResult.Usage is { } roundUsage)
            {
                totalPromptTokens += roundUsage.PromptTokens;
                totalCompletionTokens += roundUsage.CompletionTokens;
            }

            fullContent.Append(roundResult.Content ?? "");

            if (!roundResult.HasToolCalls || ++rounds > MaxToolCallRounds)
                break;

            // Record assistant turn with tool calls
            messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                roundResult.ToolCalls, roundResult.Content));

            // Reset content for next round (tool results will produce new text)
            fullContent.Clear();
            roundJobIds.Clear();

            foreach (var tc in roundResult.ToolCalls)
            {
                // ── Task-specific tool interception ──────────────
                var (handled, taskResult) = await TryHandleTaskToolAsync(tc, request.TaskContext, ct);
                if (handled)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, taskResult ?? ""));
                    var taskNotation = FormatTaskToolNotation(tc.Name);
                    fullContent.Append(taskNotation);
                    yield return ChatStreamEvent.TextDelta(taskNotation);
                    continue;
                }

                // ── Inline module tool interception ──────────────
                if (moduleRegistry.IsInlineTool(tc.Name))
                {
                    var inlineResult = await HandleInlineModuleToolAsync(
                        tc, agent.Id, channelId, threadId, ct);
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, inlineResult));
                    var inlineNotation = FormatInlineToolNotation(tc.Name);
                    fullContent.Append(inlineNotation);
                    yield return ChatStreamEvent.TextDelta(inlineNotation);
                    continue;
                }

                var parsed = ParseNativeToolCall(tc);
                if (parsed is null)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id,
                        "Error: unrecognized tool or malformed arguments."));
                    continue;
                }

                var jobRequest = await BuildJobRequestAsync(parsed, agent.Id, ct);
                var jobResponse = await jobService.SubmitAsync(channelId, jobRequest, ct);
                roundJobIds.Add(jobResponse.Id);

                // ── Inline approval ───────────────────────────────
                if (jobResponse.Status == AgentJobStatus.AwaitingApproval)
                {
                    // Check if the session user CAN approve
                    var canApprove = await CanSessionUserApproveAsync(
                        agent.Id, jobRequest.ResourceId, ct,
                        jobRequest.ActionKey);

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

                // Inject standardized tool notation into persisted content
                var notation = FormatToolNotation(jobResponse);
                fullContent.Append(notation);

                messages.Add(BuildToolResultMessage(tc.Id, jobResponse, supportsVision));
            }

            // Distribute this round's token usage across jobs submitted in the round
            if (roundJobIds.Count > 0 && roundResult.Usage is { } ru)
            {
                await jobService.RecordTokensAsync(roundJobIds, ru.PromptTokens, ru.CompletionTokens, ct);
                PatchJobCosts(jobResults, roundJobIds, ru.PromptTokens, ru.CompletionTokens);
            }
        }

        // Persist assistant message after LLM completes
        var assistantContent = fullContent.ToString();

        var assistantMessage = new ChatMessageDB
        {
            Role = "assistant",
            Content = assistantContent,
            ChannelId = channelId,
            ThreadId = threadId,
            SenderAgentId = agent.Id,
            SenderAgentName = agent.Name,
            ClientType = request.ClientType,
            PromptTokens = totalPromptTokens > 0 ? totalPromptTokens : null,
            CompletionTokens = totalCompletionTokens > 0 ? totalCompletionTokens : null
        };

        db.ChatMessages.Add(assistantMessage);
        await db.SaveChangesAsync(ct);

        if (threadId is not null)
            threadActivity.Publish(threadId.Value,
                new ThreadActivityEvent(ThreadActivityEventType.NewMessages, request.ClientType));

        var channelCost = await GetChannelCostAsync(channelId, ct);
        var threadCost = threadId is not null
            ? await GetThreadCostAsync(channelId, threadId.Value, ct)
            : null;
        var agentCost = await GetAgentCostAsync(agent.Id, ct);

        yield return ChatStreamEvent.Complete(new ChatResponse(
            ToMessageResponse(userMessage),
            ToMessageResponse(assistantMessage),
            jobResults.Count > 0 ? jobResults : null,
            channelCost,
            threadCost,
            agentCost));

        } // try
        finally
        {
            threadLock?.Dispose();
            if (isLocal)
                localModelService.ReleaseAfterChat(model.Id);
        }
    }

    /// <summary>
    /// Persists a <c>system</c>-role error message into the thread/channel
    /// so the failure is visible when the user reloads history.
    /// </summary>
    private async Task PersistStreamErrorAsync(
        Guid channelId, Guid? threadId, ChatRequest request, Exception ex,
        bool userMessageAlreadyPersisted, CancellationToken ct)
    {
        try
        {
            // If the user message was never saved (early validation failure),
            // persist it now so the history shows what the user typed.
            if (!userMessageAlreadyPersisted)
            {
                var senderUserId = jobService.GetSessionUserId();
                var senderUsername = senderUserId.HasValue
                    ? (await db.Users.Where(u => u.Id == senderUserId.Value)
                        .Select(u => u.Username).FirstOrDefaultAsync(ct))
                    : request.ExternalDisplayName ?? request.ExternalUsername;

                db.ChatMessages.Add(new ChatMessageDB
                {
                    Role = "user",
                    Content = request.Message,
                    ChannelId = channelId,
                    ThreadId = threadId,
                    SenderUserId = senderUserId,
                    SenderUsername = senderUsername,
                    ClientType = request.ClientType
                });
            }

            db.ChatMessages.Add(new ChatMessageDB
            {
                Role = "system",
                Content = $"⚠ Error: {ex.Message}",
                ChannelId = channelId,
                ThreadId = threadId,
                ClientType = request.ClientType
            });

            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Best-effort — don't mask the original exception.
        }
    }

    /// <summary>
    /// Persists a <c>system</c>-role error message into the channel/thread
    /// so the failure is visible when the user reloads history. Intended to
    /// be called by SSE/stream handlers when an exception is caught outside
    /// of the <c>IAsyncEnumerable</c> iterator.
    /// </summary>
    public async Task PersistChatErrorAsync(
        Guid channelId, Guid? threadId, ChatRequest request,
        string errorMessage, CancellationToken ct)
    {
        try
        {
            // Check whether a user message was already persisted for this
            // request. The user message is the most recent user-role message
            // matching the content + channel + thread.
            var userAlreadySaved = await db.ChatMessages.AnyAsync(
                m => m.ChannelId == channelId
                    && m.ThreadId == threadId
                    && m.Role == "user"
                    && m.Content == request.Message,
                ct);

            if (!userAlreadySaved)
            {
                var senderUserId = jobService.GetSessionUserId();
                var senderUsername = senderUserId.HasValue
                    ? (await db.Users.Where(u => u.Id == senderUserId.Value)
                        .Select(u => u.Username).FirstOrDefaultAsync(ct))
                    : request.ExternalDisplayName ?? request.ExternalUsername;

                db.ChatMessages.Add(new ChatMessageDB
                {
                    Role = "user",
                    Content = request.Message,
                    ChannelId = channelId,
                    ThreadId = threadId,
                    SenderUserId = senderUserId,
                    SenderUsername = senderUsername,
                    ClientType = request.ClientType
                });
            }

            db.ChatMessages.Add(new ChatMessageDB
            {
                Role = "system",
                Content = $"⚠ Error: {errorMessage}",
                ChannelId = channelId,
                ThreadId = threadId,
                ClientType = request.ClientType
            });

            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Best-effort — don't mask the original exception.
        }
    }

    /// <summary>
    /// Checks whether the current session user has sufficient authority
    /// to approve the given action — i.e. their own permission check
    /// would return <see cref="ClearanceVerdict.Approved"/>.
    /// </summary>
    private async Task<bool> CanSessionUserApproveAsync(
        Guid agentId, Guid? resourceId,
        CancellationToken ct, string? actionKey = null)
    {
        var userId = jobService.GetSessionUserId();
        if (userId is null) return false;

        var caller = new ActionCaller(UserId: userId);
        var result = await jobService.CheckPermissionAsync(
            agentId, resourceId, caller, ct, actionKey);

        return result.Verdict == ClearanceVerdict.Approved;
    }

    // ═══════════════════════════════════════════════════════════════
    // Task-specific tool handling
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the effective tool list for a chat call.  When a task
    /// context is present, task-specific tools (shared data, output,
    /// introspection, custom hooks) are appended to the standard set.
    /// All tool definitions come from <see cref="ModuleRegistry"/>.
    /// When a <paramref name="toolAwareness"/> filter is provided, only
    /// tools whose key is <see langword="true"/> or absent are kept.
    /// </summary>
    private IReadOnlyList<ChatToolDefinition> GetEffectiveTools(
        TaskChatContext? taskContext, Dictionary<string, bool>? toolAwareness = null)
    {
        var baseTools = new List<ChatToolDefinition>(moduleRegistry.GetAllToolDefinitions());

        if (taskContext is not null)
        {
            var store = TaskSharedData.Get(taskContext.InstanceId);
            if (store is not null)
            {
                baseTools.AddRange(BuiltInTaskTools);

                // task_output only available when the task declares [AgentOutput]
                if (store.AllowedOutputFormat is not null)
                    baseTools.Add(TaskOutputToolDef);

                // Custom [ToolCall] hooks
                baseTools.AddRange(store.CustomToolDefinitions);
            }
        }

        if (toolAwareness is null or { Count: 0 })
            return baseTools;

        // Filter: include only tools whose key is true or absent in the set.
        return baseTools
            .Where(t => !toolAwareness.TryGetValue(t.Name, out var enabled) || enabled)
            .ToList();
    }

    /// <summary>
    /// Try to handle a native tool call as a task-specific tool.
    /// Returns <c>true</c> and sets <paramref name="result"/> if handled.
    /// </summary>
    private static async Task<(bool Handled, string? Result)> TryHandleTaskToolAsync(
        ChatToolCall toolCall, TaskChatContext? taskContext, CancellationToken ct)
    {
        if (taskContext is null)
            return (false, null);

        var store = TaskSharedData.Get(taskContext.InstanceId);
        if (store is null)
            return (false, null);

        try
        {
            JsonElement? args = null;
            if (!string.IsNullOrEmpty(toolCall.ArgumentsJson))
                args = JsonDocument.Parse(toolCall.ArgumentsJson).RootElement;

            switch (toolCall.Name)
            {
                case "task_write_light_data":
                {
                    var text = args?.GetProperty("text").GetString() ?? "";
                    var ok = store.TrySetLight(text);
                    if (ok && store.OnSharedDataChanged is not null)
                        await store.OnSharedDataChanged(
                            $"Light data written ({CountWords(text)} words)",
                            store.LightData, store.BuildBigDataSnapshotJson());
                    return (true, ok
                        ? "OK: light shared data written."
                        : "Error: text exceeds the 500-word limit for light shared data.");
                }

                case "task_read_light_data":
                {
                    var val = store.LightData;
                    return (true, val ?? "(empty)");
                }

                case "task_write_big_data":
                {
                    var title = args?.GetProperty("title").GetString() ?? "Untitled";
                    var content = args?.GetProperty("content").GetString() ?? "";
                    var id = args?.TryGetProperty("id", out var idp) == true ? idp.GetString() : null;
                    var resultId = store.WriteBig(id, title, content);
                    if (store.OnSharedDataChanged is not null)
                        await store.OnSharedDataChanged(
                            $"Big data '{resultId}' written (title: {title}, {content.Length} chars)",
                            store.LightData, store.BuildBigDataSnapshotJson());
                    return (true, $"OK: big-data entry '{resultId}' written (title: {title}, {content.Length} chars).");
                }

                case "task_read_big_data":
                {
                    var id = args?.GetProperty("id").GetString() ?? "";
                    var entry = store.GetBig(id);
                    return (true, entry is not null
                        ? $"[{entry.Id}] {entry.Title}\n{entry.Content}"
                        : $"Big-data entry '{id}' not found.");
                }

                case "task_list_big_data":
                {
                    var entries = store.ListBig();
                    if (entries.Count == 0)
                        return (true, "(no big-data entries)");
                    var list = string.Join("\n", entries.Select(e => $"- {e.Id}: {e.Title}"));
                    return (true, list);
                }

                case "task_output":
                {
                    if (store.AllowedOutputFormat is null)
                        return (true, "Error: task_output is not enabled for this task. The task must declare [AgentOutput(\"format\")].");

                    var data = args?.GetProperty("data").GetString() ?? "";
                    if (store.OnAgentOutput is not null)
                        await store.OnAgentOutput(data);
                    return (true, "OK: output written to task.");
                }

                case "task_view_info":
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"Task: {store.TaskName}");
                    if (store.TaskDescription is not null)
                        sb.AppendLine($"Description: {store.TaskDescription}");
                    if (store.TaskParametersJson is not null)
                        sb.AppendLine($"Parameters: {store.TaskParametersJson}");
                    if (store.AllowedOutputFormat is not null)
                        sb.AppendLine($"Agent output format: {store.AllowedOutputFormat}");
                    return (true, sb.ToString());
                }

                case "task_view_source":
                {
                    return (true, store.TaskSourceText ?? "(source not available)");
                }
            }

            // Check custom [ToolCall] hooks
            if (store.TryGetToolHook(toolCall.Name, out var callback))
            {
                var hookResult = await callback(args, ct);
                return (true, hookResult);
            }
        }
        catch (Exception ex)
        {
            return (true, $"Error handling task tool '{toolCall.Name}': {ex.Message}");
        }

        return (false, null);
    }

    /// <summary>
    /// Dispatches an inline module tool call.  Resolves the owning module
    /// from <see cref="ModuleRegistry"/>, creates a restricted
    /// <see cref="ModuleServiceScope"/>, and calls
    /// <see cref="ISharpClawModule.ExecuteInlineToolAsync"/>.
    /// </summary>
    private async Task<string> HandleInlineModuleToolAsync(
        ChatToolCall toolCall, Guid agentId, Guid channelId, Guid? threadId, CancellationToken ct)
    {
        if (!moduleRegistry.TryResolve(toolCall.Name, out var moduleId, out var canonicalName))
            return $"Error: inline tool '{toolCall.Name}' not found in any module.";

        var module = moduleRegistry.GetModule(moduleId)
            ?? throw new InvalidOperationException(
                $"Module '{moduleId}' resolved by registry but not loaded.");

        var prefixedToolName = $"{module.ToolPrefix}_{canonicalName}";

        var context = new InlineToolContext(agentId, channelId, threadId, toolCall.Id);

        JsonElement parameters;
        try
        {
            using var doc = JsonDocument.Parse(toolCall.ArgumentsJson ?? "{}");
            parameters = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return "Error: malformed tool arguments JSON.";
        }

        // External modules use their own DI container.
        var externalHost = moduleRegistry.GetExternalHost(moduleId);
        if (externalHost is not null && !externalHost.TryAcquireExecution())
            return $"Error: module '{moduleId}' is unloading.";

        var sw = Stopwatch.StartNew();
        try
        {
            using var scope = externalHost is not null
                ? externalHost.CreateScope()
                : serviceScopeFactory.CreateScope();

            // Set ModuleExecutionContext so IModuleConfigStore resolves correctly.
            var execCtx = scope.ServiceProvider.GetService<ModuleExecutionContext>();
            if (execCtx is not null) execCtx.ModuleId = module.Id;

            var restrictedScope = new ModuleServiceScope(scope.ServiceProvider, module.Id);

            var result = await module.ExecuteInlineToolAsync(
                canonicalName, parameters, context, restrictedScope, ct);

            sw.Stop();
            metricsCollector.RecordSuccess(prefixedToolName, sw.Elapsed);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            metricsCollector.RecordFailure(prefixedToolName);
            return $"Error executing inline tool '{toolCall.Name}': {ex.Message}";
        }
        finally
        {
            externalHost?.ReleaseExecution();
        }
    }

    // ── Built-in task tool definitions ────────────────────────────

    private static readonly IReadOnlyList<ChatToolDefinition> BuiltInTaskTools = BuildBuiltInTaskTools();

    private static readonly ChatToolDefinition TaskOutputToolDef = BuildTaskOutputToolDef();

    private static IReadOnlyList<ChatToolDefinition> BuildBuiltInTaskTools()
    {
        return
        [
            new("task_write_light_data",
                "Write to light shared data (visible in header, max 500 words). Replaces previous.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {
                            "text": { "type": "string", "description": "Text (max 500 words)." }
                        },
                        "required": ["text"]
                    }
                    """)),

            new("task_read_light_data",
                "Read the current light shared data text.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)),

            new("task_write_big_data",
                "Write a large entry to big shared data. Only ID+title in header; use task_read_big_data for content.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Entry ID (auto-generated if omitted)." },
                            "title": { "type": "string", "description": "Short title." },
                            "content": { "type": "string", "description": "Full content." }
                        },
                        "required": ["title", "content"]
                    }
                    """)),

            new("task_read_big_data",
                "Read a big shared data entry by ID.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Entry ID." }
                        },
                        "required": ["id"]
                    }
                    """)),

            new("task_list_big_data",
                "List big shared data entries (IDs and titles).",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)),

            new("task_view_info",
                "View task metadata (name, description, parameters, output format).",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)),

            new("task_view_source",
                "View the task definition source code.",
                BuildJsonSchema("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)),
        ];
    }

    private static ChatToolDefinition BuildTaskOutputToolDef()
    {
        return new("task_output",
            "Write structured output to the task. Format must match [AgentOutput] annotation.",
            BuildJsonSchema("""
                {
                    "type": "object",
                    "properties": {
                        "data": { "type": "string", "description": "Output data." }
                    },
                    "required": ["data"]
                }
                """));
    }

    private static JsonElement BuildJsonSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Builds <see cref="ChatToolDefinition"/>s from a task's
    /// <see cref="TaskToolCallHook"/>s for registration in the
    /// shared data store.
    /// </summary>
    public static IReadOnlyList<ChatToolDefinition> BuildCustomToolDefinitions(
        IReadOnlyList<Infrastructure.Tasks.Models.TaskToolCallHook> hooks)
    {
        if (hooks.Count == 0) return [];

        var defs = new List<ChatToolDefinition>(hooks.Count);
        foreach (var hook in hooks)
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var param in hook.Parameters)
            {
                var prop = new Dictionary<string, string> { ["type"] = MapTypeToJsonType(param.TypeName) };
                if (param.Description is not null)
                    prop["description"] = param.Description;
                properties[param.Name] = prop;
                required.Add(param.Name);
            }

            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties,
            };
            if (required.Count > 0)
                schema["required"] = required;

            var schemaJson = JsonSerializer.Serialize(schema);
            defs.Add(new ChatToolDefinition(
                hook.Name,
                hook.Description ?? $"Custom task tool: {hook.Name}",
                BuildJsonSchema(schemaJson)));
        }

        return defs;
    }

    private static string MapTypeToJsonType(string csharpType) => csharpType switch
    {
        "int" or "long" or "double" or "float" or "decimal" => "number",
        "bool" => "boolean",
        _ => "string"
    };

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else if (!inWord) { inWord = true; count++; }
        }
        return count;
    }

    /// <summary>
    /// Patches <paramref name="jobResults"/> entries whose IDs appear in
    /// <paramref name="roundJobIds"/> with the correct <see cref="TokenUsageResponse"/>
    /// computed from the same even-split logic used by
    /// <see cref="AgentJobService.RecordTokensAsync"/>.
    /// This fixes the timing gap where job snapshots are captured at submit
    /// time, before the tokens have been written to the database.
    /// </summary>
    private static void PatchJobCosts(
        List<AgentJobResponse> jobResults, IReadOnlyList<Guid> roundJobIds,
        int promptTokens, int completionTokens)
    {
        if (roundJobIds.Count == 0) return;

        var count = roundJobIds.Count;
        var promptPer = promptTokens / count;
        var completionPer = completionTokens / count;
        var promptRemainder = promptTokens % count;
        var completionRemainder = completionTokens % count;

        // Walk the round IDs in order; the first gets the remainder (mirrors RecordTokensAsync).
        for (var ri = 0; ri < roundJobIds.Count; ri++)
        {
            var id = roundJobIds[ri];
            var p = promptPer + (ri == 0 ? promptRemainder : 0);
            var c = completionPer + (ri == 0 ? completionRemainder : 0);

            for (var ji = jobResults.Count - 1; ji >= 0; ji--)
            {
                if (jobResults[ji].Id != id) continue;

                // Accumulate: the snapshot may already carry tokens from a previous round.
                var existing = jobResults[ji].JobCost;
                var newP = (existing?.TotalPromptTokens ?? 0) + p;
                var newC = (existing?.TotalCompletionTokens ?? 0) + c;

                jobResults[ji] = jobResults[ji] with
                {
                    JobCost = new TokenUsageResponse(newP, newC, newP + newC)
                };
                break;
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="SubmitAgentJobRequest"/> from a parsed tool call.
    /// </summary>
    private async Task<SubmitAgentJobRequest> BuildJobRequestAsync(
        ParsedToolCall parsed, Guid agentId, CancellationToken ct)
    {
        return new SubmitAgentJobRequest(
            ActionKey: parsed.ActionKey,
            ResourceId: parsed.ResourceId,
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
        ModelCapability modelCapabilities,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
        CancellationToken ct,
        TaskChatContext? taskContext = null,
        Dictionary<string, bool>? toolAwareness = null,
        Guid? threadId = null)
    {
        var messages = new List<ToolAwareMessage>(dbHistory.Count);
        foreach (var msg in dbHistory)
            messages.Add(new ToolAwareMessage { Role = msg.Role, Content = msg.Content });

        var supportsVision = modelCapabilities.HasFlag(ModelCapability.Vision);
        var jobResults = new List<AgentJobResponse>();
        var toolNotation = new StringBuilder();
        var rounds = 0;
        var effectiveTools = GetEffectiveTools(taskContext, toolAwareness);
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var roundJobIds = new List<Guid>();

        while (true)
        {
            var result = await client.ChatCompletionWithToolsAsync(
                httpClient, apiKey, modelName, systemPrompt, messages, effectiveTools, maxCompletionTokens, providerParameters, completionParameters, ct);

            if (result.Usage is { } usage)
            {
                totalPromptTokens += usage.PromptTokens;
                totalCompletionTokens += usage.CompletionTokens;
            }

            if (!result.HasToolCalls || ++rounds > MaxToolCallRounds)
            {
                // Compose final content: tool notation followed by model text
                var finalContent = toolNotation.Length > 0
                    ? toolNotation.ToString() + "\n" + (result.Content ?? "")
                    : result.Content ?? "";
                return new ToolLoopResult(finalContent, jobResults, totalPromptTokens, totalCompletionTokens);
            }

            // Record assistant turn with tool calls
            messages.Add(ToolAwareMessage.AssistantWithToolCalls(result.ToolCalls, result.Content));

            var anyUnresolvableApproval = false;
            roundJobIds.Clear();

            foreach (var tc in result.ToolCalls)
            {
                // ── Task-specific tool interception ──────────────
                var (handled, taskResult) = await TryHandleTaskToolAsync(tc, taskContext, ct);
                if (handled)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, taskResult ?? ""));
                    toolNotation.Append(FormatTaskToolNotation(tc.Name));
                    continue;
                }

                // ── Inline module tool interception ──────────────
                if (moduleRegistry.IsInlineTool(tc.Name))
                {
                    var inlineResult = await HandleInlineModuleToolAsync(
                        tc, agentId, channelId, threadId, ct);
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, inlineResult));
                    toolNotation.Append(FormatInlineToolNotation(tc.Name));
                    continue;
                }

                var parsed = ParseNativeToolCall(tc);
                if (parsed is null)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id,
                        "Error: unrecognized tool or malformed arguments."));
                    continue;
                }

                var jobRequest = await BuildJobRequestAsync(parsed, agentId, ct);

                var jobResponse = await jobService.SubmitAsync(channelId, jobRequest, ct);
                roundJobIds.Add(jobResponse.Id);

                // ── Inline approval (when callback available) ────
                if (jobResponse.Status == AgentJobStatus.AwaitingApproval
                    && approvalCallback is not null)
                {
                    var canApprove = await CanSessionUserApproveAsync(
                        agentId, jobRequest.ResourceId, ct,
                        jobRequest.ActionKey);

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

                // Record standardized tool notation for persistence
                toolNotation.Append(FormatToolNotation(jobResponse));

                messages.Add(BuildToolResultMessage(tc.Id, jobResponse, supportsVision));

                if (jobResponse.Status == AgentJobStatus.AwaitingApproval)
                    anyUnresolvableApproval = true;
            }

            // Distribute this round's token usage across jobs submitted in the round
            if (roundJobIds.Count > 0 && result.Usage is { } ru)
            {
                await jobService.RecordTokensAsync(roundJobIds, ru.PromptTokens, ru.CompletionTokens, ct);
                PatchJobCosts(jobResults, roundJobIds, ru.PromptTokens, ru.CompletionTokens);
            }

            if (anyUnresolvableApproval)
            {
                var finalResult = await client.ChatCompletionWithToolsAsync(
                    httpClient, apiKey, modelName, systemPrompt, messages, effectiveTools, maxCompletionTokens, providerParameters, completionParameters, ct);
                if (finalResult.Usage is { } finalUsage)
                {
                    totalPromptTokens += finalUsage.PromptTokens;
                    totalCompletionTokens += finalUsage.CompletionTokens;
                }
                var finalContent = toolNotation.Length > 0
                    ? toolNotation.ToString() + "\n" + (finalResult.Content ?? "")
                    : finalResult.Content ?? "";
                return new ToolLoopResult(finalContent, jobResults, totalPromptTokens, totalCompletionTokens);
            }
        }
    }

    /// <summary>
    /// Simple single-call completion for providers without native tool support
    /// or when tools are explicitly disabled.
    /// </summary>
    private static async Task<ToolLoopResult> RunPlainCompletionAsync(
        IProviderApiClient client,
        HttpClient httpClient,
        string apiKey,
        string modelName,
        string systemPrompt,
        List<ChatCompletionMessage> history,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters,
        CancellationToken ct)
    {
        var result = await client.ChatCompletionAsync(
            httpClient, apiKey, modelName, systemPrompt, history,
            maxCompletionTokens, providerParameters, completionParameters, ct);

        return new ToolLoopResult(
            result.Content ?? "",
            [],
            result.Usage?.PromptTokens ?? 0,
            result.Usage?.CompletionTokens ?? 0);
    }

    /// <summary>
    /// Parses a native <see cref="ChatToolCall"/> into the internal
    /// <see cref="ParsedToolCall"/> representation. Returns <see langword="null"/>
    /// if the tool name is unrecognized or the arguments are malformed.
    /// All tool definitions are resolved via <see cref="ModuleRegistry"/>.
    /// </summary>
    private ParsedToolCall? ParseNativeToolCall(ChatToolCall toolCall)
    {
        if (moduleRegistry.TryResolve(toolCall.Name, out var moduleId, out var toolName))
        {
            Debug.WriteLine(
                $"[ParseToolCall] Module tool: {toolCall.Name} → {moduleId}.{toolName}",
                "SharpClaw.CLI");

            // Build the module envelope as ScriptJson so DispatchModuleExecutionAsync
            // can deserialize it on the job pipeline side.
            var envelope = JsonSerializer.Serialize(
                new { module = moduleId, tool = toolName, @params = JsonDocument.Parse(toolCall.ArgumentsJson ?? "{}").RootElement },
                SecureJsonOptions.Envelope);

            // Attempt to extract resourceId from the arguments.
            // Modules use either "resource_id" or "targetId" as the parameter name.
            Guid? modResourceId = null;
            try
            {
                using var doc = JsonDocument.Parse(toolCall.ArgumentsJson ?? "{}");
                if ((doc.RootElement.TryGetProperty("resource_id", out var rp)
                     || doc.RootElement.TryGetProperty("targetId", out rp))
                    && Guid.TryParse(rp.GetString(), out var mrid))
                    modResourceId = mrid;
            }
            catch (JsonException) { /* non-critical */ }

            Debug.WriteLine(
                $"[ParseToolCall] ResourceId={modResourceId?.ToString() ?? "(null)"} from args: {toolCall.ArgumentsJson}",
                "SharpClaw.CLI");

            return new ParsedToolCall(
                toolCall.Id,
                modResourceId,
                SandboxId: null,
                ScriptJson: envelope,
                ActionKey: toolCall.Name);
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Screenshot extraction & vision-aware tool results
    // ═══════════════════════════════════════════════════════════════

    private const string ScreenshotMarker = "[SCREENSHOT_BASE64]";

    /// <summary>
    /// If the job's <see cref="AgentJobResponse.ResultData"/> contains a
    /// <c>[SCREENSHOT_BASE64]</c> marker, splits it into the descriptive
    /// text and the raw base64 data. Otherwise returns the original
    /// <see cref="AgentJobResponse.ResultData"/> with no image.
    /// </summary>
    private static (string? TextResult, string? ImageBase64) ExtractScreenshotData(AgentJobResponse job)
    {
        if (job.ResultData is null)
            return (null, null);

        var markerIndex = job.ResultData.IndexOf(ScreenshotMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return (job.ResultData, null);

        var textPart = job.ResultData[..markerIndex].TrimEnd();
        var base64Part = job.ResultData[(markerIndex + ScreenshotMarker.Length)..];
        return (textPart, base64Part);
    }

    /// <summary>
    /// Builds a <see cref="ToolAwareMessage"/> for a tool result. When the
    /// result contains screenshot data and the model supports vision, the
    /// image is attached as a multipart content block. Otherwise, only the
    /// text portion is included (the base64 blob is omitted for non-vision
    /// models to avoid wasting context).
    /// </summary>
    private static ToolAwareMessage BuildToolResultMessage(
        string toolCallId, AgentJobResponse job, bool supportsVision)
    {
        var (textResult, imageBase64) = ExtractScreenshotData(job);

        var resultContent =
            $"status={job.Status}" +
            (textResult is not null ? $" result={textResult}" : "") +
            (job.ErrorLog is not null ? $" error={job.ErrorLog}" : "");

        if (imageBase64 is not null && supportsVision)
        {
            return ToolAwareMessage.ToolResultWithImage(
                toolCallId, resultContent, imageBase64, "image/png");
        }

        // Non-vision model: append a note that the screenshot was captured
        // but cannot be displayed, so the model knows it succeeded.
        if (imageBase64 is not null)
        {
            resultContent += " (screenshot captured successfully)";
        }

        return ToolAwareMessage.ToolResult(toolCallId, resultContent);
    }

    // ═══════════════════════════════════════════════════════════════
    // System prompt & tool definitions (loaded from embedded resources)
    // ═══════════════════════════════════════════════════════════════

    private static string BuildSystemPrompt(string? agentPrompt)
    {
        if (string.IsNullOrEmpty(agentPrompt))
            return NativeToolSystemSuffix;

        return agentPrompt + "\n\n" + NativeToolSystemSuffix;
    }

    private static readonly string NativeToolSystemSuffix =
        LoadEmbeddedResource("SharpClaw.Application.Core.tool-instructions-native-suffix.md");

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

    private sealed record ParsedToolCall(
        string CallId,
        Guid? ResourceId,
        string? SandboxId,
        string? ScriptJson,
        DangerousShellType? DangerousShellType = null,
        SafeShellType? SafeShellType = null,
        Guid? TranscriptionModelId = null,
        string? Language = null,
        string? WorkingDirectory = null,
        string? RawJson = null,
        string? ActionKey = null);

    private readonly record struct ToolLoopResult(
        string AssistantContent,
        List<AgentJobResponse> JobResults,
        int TotalPromptTokens = 0,
        int TotalCompletionTokens = 0);

    // ═══════════════════════════════════════════════════════════════
    // Tool call notation (persisted in assistant message content)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Formats a standardized tool call notation line for a job that
    /// was submitted and executed (no approval flow).
    /// Format: <c>\n⚙ [ActionKey] → Status</c>
    /// </summary>
    private static string FormatToolNotation(AgentJobResponse job)
        => $"\n⚙ [{job.ActionKey ?? "unknown"}] → {job.Status}";

    /// <summary>
    /// Formats a tool call notation line for a job that required
    /// approval, showing the final outcome.
    /// Format: <c>\n⏳ [ActionKey] awaiting approval → Status</c>
    /// </summary>
    private static string FormatApprovalNotation(AgentJobResponse job)
        => $"\n⏳ [{job.ActionKey ?? "unknown"}] awaiting approval → {job.Status}";

    /// <summary>
    /// Formats a tool call notation line for an inline tool (wait,
    /// list_accessible_threads, etc.) that does not go through the
    /// job pipeline.
    /// Format: <c>\n⚙ [tool_name] → done</c>
    /// </summary>
    private static string FormatInlineToolNotation(string toolName)
        => $"\n⚙ [{toolName}] → done";

    /// <summary>
    /// Formats a tool call notation line for task-specific tools.
    /// Format: <c>\n⚙ [tool_name] → done</c>
    /// </summary>
    private static string FormatTaskToolNotation(string toolName)
        => $"\n⚙ [{toolName}] → done";

    /// <summary>
    /// Maps the typed provider parameter fields from <see cref="AgentDB"/> into
    /// a <see cref="CompletionParameters"/> instance. Returns <see langword="null"/>
    /// when all fields are their default (null) values.
    /// </summary>
    private static CompletionParameters? BuildCompletionParameters(AgentDB agent)
    {
        var cp = new CompletionParameters
        {
            Temperature = agent.Temperature,
            TopP = agent.TopP,
            TopK = agent.TopK,
            FrequencyPenalty = agent.FrequencyPenalty,
            PresencePenalty = agent.PresencePenalty,
            Stop = agent.Stop,
            Seed = agent.Seed,
            ResponseFormat = agent.ResponseFormat,
            ReasoningEffort = agent.ReasoningEffort,
        };
        return cp.IsEmpty ? null : cp;
    }
}
