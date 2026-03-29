using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.DTOs.Tasks;
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
    AgentJobService jobService,
    LocalModelService localModelService,
    HeaderTagProcessor headerTagProcessor,
    ThreadActivitySignal threadActivity,
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

    public async Task<ChatResponse> SendMessageAsync(
        Guid channelId, ChatRequest request,
        Guid? threadId = null,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback = null,
        CancellationToken ct = default)
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

        var isLocal = provider.ProviderType == ProviderType.Local;
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

        var apiKey = isLocal ? "local" : ApiKeyEncryptor.Decrypt(provider.EncryptedApiKey!, encryptionOptions.Key);
        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);
        if (client is LocalInferenceApiClient lic)
            lic.CurrentModelId = model.Id;
        var useNativeTools = client.SupportsNativeToolCalling;
        var disableTools = channel.DisableToolSchemas || agent.DisableToolSchemas;
        var systemPrompt = disableTools
            ? agent.SystemPrompt ?? ""
            : BuildSystemPrompt(agent.SystemPrompt, useNativeTools);

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
        var toolAwareness = disableTools ? null : (channel.ToolAwarenessSet?.Tools ?? agent.ToolAwarenessSet?.Tools);

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

        var loopResult = disableTools
            ? await RunTextToolLoopAsync(
                client, httpClient, apiKey, model.Name, systemPrompt,
                history, agent.Id, channelId, modelCapabilities, maxTokens, providerParams, completionParams, approvalCallback, ct,
                taskContext: request.TaskContext)
            : useNativeTools
            ? await RunNativeToolLoopAsync(
                client, httpClient, apiKey, model.Name, systemPrompt,
                history, agent.Id, channelId, modelCapabilities, maxTokens, providerParams, completionParams, approvalCallback, ct,
                taskContext: request.TaskContext, toolAwareness: toolAwareness)
            : await RunTextToolLoopAsync(
                client, httpClient, apiKey, model.Name, systemPrompt,
                history, agent.Id, channelId, modelCapabilities, maxTokens, providerParams, completionParams, approvalCallback, ct,
                taskContext: request.TaskContext);

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

        return new ChatResponse(
            ToMessageResponse(userMessage),
            ToMessageResponse(assistantMessage),
            loopResult.JobResults.Count > 0 ? loopResult.JobResults : null,
            channelCost,
            threadCost);

        } // try
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
        var query = threadId is not null
            ? db.ChatMessages.Where(m => m.ThreadId == threadId)
            : db.ChatMessages.Where(m => m.ChannelId == channelId);

        return await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new ChatMessageResponse(
                m.Role, m.Content, m.CreatedAt,
                m.SenderUserId, m.SenderUsername,
                m.SenderAgentId, m.SenderAgentName,
                m.ClientType != null ? m.ClientType.ToString() : null))
            .ToListAsync(ct);
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
        var rows = await db.ChatMessages
            .Where(m => m.ChannelId == channelId && m.PromptTokens != null)
            .GroupBy(m => new { m.SenderAgentId, m.SenderAgentName })
            .Select(g => new
            {
                g.Key.SenderAgentId,
                g.Key.SenderAgentName,
                PromptTokens = g.Sum(m => m.PromptTokens!.Value),
                CompletionTokens = g.Sum(m => m.CompletionTokens ?? 0)
            })
            .ToListAsync(ct);

        var breakdown = rows
            .Where(r => r.SenderAgentId.HasValue)
            .Select(r => new AgentTokenBreakdown(
                r.SenderAgentId!.Value,
                r.SenderAgentName ?? "Unknown",
                r.PromptTokens,
                r.CompletionTokens,
                r.PromptTokens + r.CompletionTokens))
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

        var rows = await db.ChatMessages
            .Where(m => m.ThreadId == threadId && m.PromptTokens != null)
            .GroupBy(m => new { m.SenderAgentId, m.SenderAgentName })
            .Select(g => new
            {
                g.Key.SenderAgentId,
                g.Key.SenderAgentName,
                PromptTokens = g.Sum(m => m.PromptTokens!.Value),
                CompletionTokens = g.Sum(m => m.CompletionTokens ?? 0)
            })
            .ToListAsync(ct);

        var breakdown = rows
            .Where(r => r.SenderAgentId.HasValue)
            .Select(r => new AgentTokenBreakdown(
                r.SenderAgentId!.Value,
                r.SenderAgentName ?? "Unknown",
                r.PromptTokens,
                r.CompletionTokens,
                r.PromptTokens + r.CompletionTokens))
            .OrderByDescending(b => b.TotalTokens)
            .ToList();

        var totalPrompt = breakdown.Sum(b => b.PromptTokens);
        var totalCompletion = breakdown.Sum(b => b.CompletionTokens);

        return new ThreadCostResponse(
            threadId, channelId, totalPrompt, totalCompletion,
            totalPrompt + totalCompletion, breakdown);
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

        // Fetch up to maxMessages most-recent messages, chronologically.
        var messages = await db.ChatMessages
            .Where(m => m.ThreadId == threadId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxMessages)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatCompletionMessage(m.Role, m.Content))
            .ToListAsync(ct);

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
            return await BuildTaskChatHeaderAsync(channel, agent, taskContext, ct);

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
            return await BuildExternalUserHeaderAsync(channel, agent, clientType, externalUsername, externalDisplayName, ct);

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
                .Include(p => p.DisplayDeviceAccesses)
                .Include(p => p.EditorSessionAccesses)
                .Include(p => p.AgentPermissions)
                .Include(p => p.TaskPermissions)
                .Include(p => p.SkillPermissions)
                .Include(p => p.AgentHeaderAccesses)
                .Include(p => p.ChannelHeaderAccesses)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == psId, ct);
        }

        var sb = new StringBuilder();
        sb.Append("[time: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        sb.Append(" | user: ").Append(user.Username);
        sb.Append(" | via: ").Append(clientType);

        if (user.Role is not null && ps is not null)
        {
            var grants = CollectGrants(ps);

            if (grants.Count > 0)
                sb.Append(" | role: ").Append(user.Role.Name)
                  .Append(" (").Append(string.Join(", ", grants)).Append(')');
            else
                sb.Append(" | role: ").Append(user.Role.Name);
        }

        if (!string.IsNullOrWhiteSpace(user.Bio))
            sb.Append(" | bio: ").Append(user.Bio);

        // ── Agent self-awareness: own role, clearance, permissions ─
        var agentWithRole = await db.Agents
            .Include(a => a.Role)
            .ThenInclude(r => r!.PermissionSet)
            .FirstOrDefaultAsync(a => a.Id == agent.Id, ct);

        if (agentWithRole?.Role is { } agentRole)
        {
            PermissionSetDB? agentPs = null;
            if (agentRole.PermissionSetId is { } agentPsId)
            {
                agentPs = await db.PermissionSets
                    .Include(p => p.SafeShellAccesses)
                    .Include(p => p.ContainerAccesses)
                    .Include(p => p.WebsiteAccesses)
                    .Include(p => p.SearchEngineAccesses)
                    .Include(p => p.LocalInfoStorePermissions)
                    .Include(p => p.ExternalInfoStorePermissions)
                    .Include(p => p.AudioDeviceAccesses)
                    .Include(p => p.DisplayDeviceAccesses)
                    .Include(p => p.EditorSessionAccesses)
                    .Include(p => p.AgentPermissions)
                    .Include(p => p.TaskPermissions)
                    .Include(p => p.SkillPermissions)
                    .Include(p => p.AgentHeaderAccesses)
                    .Include(p => p.ChannelHeaderAccesses)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(p => p.Id == agentPsId, ct);
            }

            sb.Append(" | agent-role: ").Append(agentRole.Name);
            if (agentPs is not null)
            {
                sb.Append(" clearance=").Append(agentPs.DefaultClearance);
                var agentGrants = await CollectGrantsWithResourcesAsync(agentPs, ct);
                if (agentGrants.Count > 0)
                    sb.Append(" (").Append(string.Join(", ", agentGrants)).Append(')');
                else
                    sb.Append(" (no grants)");
            }
        }
        else
        {
            // Agent has no role — emit a clear notice so the model knows it
            // cannot use any tools that require permissions.
            sb.Append(" | agent-role: (none) clearance=Unset (no permissions)");
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
        return sb.ToString();
    }

    /// <summary>
    /// Builds a header for bot-forwarded messages where there is no
    /// authenticated DB user.  Uses the external username/display name
    /// provided by the gateway from the messaging platform metadata.
    /// </summary>
    private async Task<string> BuildExternalUserHeaderAsync(
        ChannelDB channel, AgentDB agent, ChatClientType clientType,
        string externalUsername, string? externalDisplayName, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("[time: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        sb.Append(" | user: ").Append(externalDisplayName ?? externalUsername);
        if (externalDisplayName is not null && externalUsername != externalDisplayName)
            sb.Append(" (@").Append(externalUsername).Append(')');
        sb.Append(" | via: ").Append(clientType);

        // Agent self-awareness (same logic as the authenticated path)
        var agentWithRole = await db.Agents
            .Include(a => a.Role)
            .ThenInclude(r => r!.PermissionSet)
            .FirstOrDefaultAsync(a => a.Id == agent.Id, ct);

        if (agentWithRole?.Role is { } agentRole)
        {
            PermissionSetDB? agentPs = null;
            if (agentRole.PermissionSetId is { } agentPsId)
            {
                agentPs = await db.PermissionSets
                    .Include(p => p.SafeShellAccesses)
                    .Include(p => p.ContainerAccesses)
                    .Include(p => p.WebsiteAccesses)
                    .Include(p => p.SearchEngineAccesses)
                    .Include(p => p.LocalInfoStorePermissions)
                    .Include(p => p.ExternalInfoStorePermissions)
                    .Include(p => p.AudioDeviceAccesses)
                    .Include(p => p.DisplayDeviceAccesses)
                    .Include(p => p.EditorSessionAccesses)
                    .Include(p => p.AgentPermissions)
                    .Include(p => p.TaskPermissions)
                    .Include(p => p.SkillPermissions)
                    .Include(p => p.AgentHeaderAccesses)
                    .Include(p => p.ChannelHeaderAccesses)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(p => p.Id == agentPsId, ct);
            }

            sb.Append(" | agent-role: ").Append(agentRole.Name);
            if (agentPs is not null)
            {
                sb.Append(" clearance=").Append(agentPs.DefaultClearance);
                var agentGrants = await CollectGrantsWithResourcesAsync(agentPs, ct);
                if (agentGrants.Count > 0)
                    sb.Append(" (").Append(string.Join(", ", agentGrants)).Append(')');
                else
                    sb.Append(" (no grants)");
            }
        }
        else
        {
            sb.Append(" | agent-role: (none) clearance=Unset (no permissions)");
        }

        sb.AppendLine("]");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a compact header for messages originating from an automated
    /// task.  Unlike the user header, there is no session user — the
    /// header identifies the task by name so the agent understands the
    /// request is automated.  Includes light-shared-data inline and
    /// big-shared-data IDs for tool-based retrieval.
    /// </summary>
    private async Task<string> BuildTaskChatHeaderAsync(
        ChannelDB channel, AgentDB agent, TaskChatContext taskContext, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("[time: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        sb.Append(" | source: automated task");
        sb.Append(" | task: ").Append(taskContext.TaskName);

        // ── Shared data visible to the model ──
        var store = TaskSharedData.Get(taskContext.InstanceId);
        if (store is not null)
        {
            // Light data — plain text (≤500 words)
            var lightText = store.LightData;
            if (lightText is not null)
            {
                sb.Append(" | shared-data: ").Append(lightText);
            }

            // Big data — IDs + titles only (use task_read_big_data to get content)
            var bigEntries = store.ListBig();
            if (bigEntries.Count > 0)
            {
                sb.Append(" | big-data-ids: [");
                sb.Append(string.Join(", ", bigEntries.Select(e => $"{e.Id}:\"{e.Title}\"")));
                sb.Append(']');
            }
        }

        // ── Agent self-awareness (same as user header) ──
        var agentWithRole = await db.Agents
            .Include(a => a.Role)
            .ThenInclude(r => r!.PermissionSet)
            .FirstOrDefaultAsync(a => a.Id == agent.Id, ct);

        if (agentWithRole?.Role is { } agentRole)
        {
            PermissionSetDB? agentPs = null;
            if (agentRole.PermissionSetId is { } agentPsId)
            {
                agentPs = await db.PermissionSets
                    .Include(p => p.SafeShellAccesses)
                    .Include(p => p.ContainerAccesses)
                    .Include(p => p.WebsiteAccesses)
                    .Include(p => p.SearchEngineAccesses)
                    .Include(p => p.LocalInfoStorePermissions)
                    .Include(p => p.ExternalInfoStorePermissions)
                    .Include(p => p.AudioDeviceAccesses)
                    .Include(p => p.DisplayDeviceAccesses)
                    .Include(p => p.EditorSessionAccesses)
                    .Include(p => p.AgentPermissions)
                    .Include(p => p.TaskPermissions)
                    .Include(p => p.SkillPermissions)
                    .Include(p => p.AgentHeaderAccesses)
                    .Include(p => p.ChannelHeaderAccesses)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(p => p.Id == agentPsId, ct);
            }

            sb.Append(" | agent-role: ").Append(agentRole.Name);
            if (agentPs is not null)
            {
                sb.Append(" clearance=").Append(agentPs.DefaultClearance);
                var agentGrants = await CollectGrantsWithResourcesAsync(agentPs, ct);
                if (agentGrants.Count > 0)
                    sb.Append(" (").Append(string.Join(", ", agentGrants)).Append(')');
                else
                    sb.Append(" (no grants)");
            }
        }
        else
        {
            sb.Append(" | agent-role: (none) clearance=Unset (no permissions)");
        }

        sb.AppendLine("]");
        return sb.ToString();
    }

    /// <summary>
    /// Collects human-readable grant names from a permission set for
    /// inclusion in the chat header.
    /// </summary>
    private static List<string> CollectGrants(PermissionSetDB ps)
    {
        var grants = new List<string>();
        if (ps.CanCreateSubAgents) grants.Add("CreateSubAgents");
        if (ps.CanCreateContainers) grants.Add("CreateContainers");
        if (ps.CanRegisterInfoStores) grants.Add("RegisterInfoStores");
        if (ps.CanAccessLocalhostInBrowser) grants.Add("LocalhostBrowser");
        if (ps.CanAccessLocalhostCli) grants.Add("LocalhostCli");
        if (ps.CanClickDesktop) grants.Add("ClickDesktop");
        if (ps.CanTypeOnDesktop) grants.Add("TypeOnDesktop");
        if (ps.CanReadCrossThreadHistory) grants.Add("ReadCrossThreadHistory");
        if (ps.DangerousShellAccesses.Count > 0) grants.Add("DangerousShell");
        if (ps.SafeShellAccesses.Count > 0) grants.Add("SafeShell");
        if (ps.ContainerAccesses.Count > 0) grants.Add("ContainerAccess");
        if (ps.WebsiteAccesses.Count > 0) grants.Add("WebsiteAccess");
        if (ps.SearchEngineAccesses.Count > 0) grants.Add("SearchEngineAccess");
        if (ps.LocalInfoStorePermissions.Count > 0) grants.Add("LocalInfoStore");
        if (ps.ExternalInfoStorePermissions.Count > 0) grants.Add("ExternalInfoStore");
        if (ps.AudioDeviceAccesses.Count > 0) grants.Add("AudioDevice");
        if (ps.DisplayDeviceAccesses.Count > 0) grants.Add("DisplayDevice");
        if (ps.EditorSessionAccesses.Count > 0) grants.Add("EditorSession");
        if (ps.AgentPermissions.Count > 0) grants.Add("ManageAgent");
        if (ps.TaskPermissions.Count > 0) grants.Add("EditTask");
        if (ps.SkillPermissions.Count > 0) grants.Add("AccessSkill");
        return grants;
    }

    /// <summary>
    /// Collects grant names with enumerated resource IDs for the agent
    /// self-awareness header. When a wildcard grant
    /// (<see cref="WellKnownIds.AllResources"/>) is present, all resource
    /// IDs of that type are resolved from the database so the agent knows
    /// exactly which resources it can act on.
    /// </summary>
    private async Task<List<string>> CollectGrantsWithResourcesAsync(
        PermissionSetDB ps, CancellationToken ct)
    {
        var grants = new List<string>();
        if (ps.CanCreateSubAgents) grants.Add("CreateSubAgents");
        if (ps.CanCreateContainers) grants.Add("CreateContainers");
        if (ps.CanRegisterInfoStores) grants.Add("RegisterInfoStores");
        if (ps.CanAccessLocalhostInBrowser) grants.Add("LocalhostBrowser");
        if (ps.CanAccessLocalhostCli) grants.Add("LocalhostCli");
        if (ps.CanClickDesktop) grants.Add("ClickDesktop");
        if (ps.CanTypeOnDesktop) grants.Add("TypeOnDesktop");
        if (ps.CanReadCrossThreadHistory) grants.Add("ReadCrossThreadHistory");

        await AppendResourceGrantAsync(grants, "DangerousShell",
            ps.DangerousShellAccesses.Select(a => a.SystemUserId),
            () => db.SystemUsers.Select(s => s.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "SafeShell",
            ps.SafeShellAccesses.Select(a => a.ContainerId),
            () => db.Containers.Select(c => c.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "ContainerAccess",
            ps.ContainerAccesses.Select(a => a.ContainerId),
            () => db.Containers.Select(c => c.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "WebsiteAccess",
            ps.WebsiteAccesses.Select(a => a.WebsiteId),
            () => db.Websites.Select(w => w.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "SearchEngineAccess",
            ps.SearchEngineAccesses.Select(a => a.SearchEngineId),
            () => db.SearchEngines.Select(s => s.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "LocalInfoStore",
            ps.LocalInfoStorePermissions.Select(a => a.LocalInformationStoreId),
            () => db.LocalInformationStores.Select(l => l.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "ExternalInfoStore",
            ps.ExternalInfoStorePermissions.Select(a => a.ExternalInformationStoreId),
            () => db.ExternalInformationStores.Select(e => e.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "AudioDevice",
            ps.AudioDeviceAccesses.Select(a => a.AudioDeviceId),
            () => db.AudioDevices.Select(a => a.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "DisplayDevice",
            ps.DisplayDeviceAccesses.Select(a => a.DisplayDeviceId),
            () => db.DisplayDevices.Select(d => d.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "EditorSession",
            ps.EditorSessionAccesses.Select(a => a.EditorSessionId),
            () => db.EditorSessions.Select(e => e.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "ManageAgent",
            ps.AgentPermissions.Select(a => a.AgentId),
            () => db.Agents.Select(a => a.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "EditTask",
            ps.TaskPermissions.Select(a => a.ScheduledTaskId),
            () => db.ScheduledTasks.Select(t => t.Id).ToListAsync(ct), ct);

        await AppendResourceGrantAsync(grants, "AccessSkill",
            ps.SkillPermissions.Select(a => a.SkillId),
            () => db.Skills.Select(s => s.Id).ToListAsync(ct), ct);

        return grants;
    }

    /// <summary>
    /// Appends a grant entry with resource IDs. If any grant entry
    /// matches <see cref="WellKnownIds.AllResources"/>, all IDs of that
    /// type are loaded from the database so the agent sees the resolved
    /// list instead of the wildcard.
    /// </summary>
    private static async Task AppendResourceGrantAsync(
        List<string> grants,
        string grantName,
        IEnumerable<Guid> grantedIds,
        Func<Task<List<Guid>>> loadAllIdsAsync,
        CancellationToken ct)
    {
        var ids = grantedIds.ToList();
        if (ids.Count == 0)
            return;

        List<Guid> resolved;
        if (ids.Any(id => id == WellKnownIds.AllResources))
        {
            resolved = await loadAllIdsAsync();
        }
        else
        {
            resolved = ids;
        }

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

        var isLocal = provider.ProviderType == ProviderType.Local;
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

        var apiKey = isLocal ? "local" : ApiKeyEncryptor.Decrypt(provider.EncryptedApiKey!, encryptionOptions.Key);
        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);
        if (client is LocalInferenceApiClient streamLic)
            streamLic.CurrentModelId = model.Id;
        var systemPrompt = channel.DisableToolSchemas || agent.DisableToolSchemas
            ? agent.SystemPrompt ?? ""
            : BuildSystemPrompt(agent.SystemPrompt, nativeToolCalling: true);

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

            foreach (var tc in roundResult.ToolCalls)
            {
                // ── Task-specific tool interception ──────────────
                var (handled, taskResult) = await TryHandleTaskToolAsync(tc, request.TaskContext, ct);
                if (handled)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, taskResult ?? ""));
                    continue;
                }

                // ── Inline tool interception (no permissions) ────
                var (inlineHandled, inlineResult) = await TryHandleInlineToolAsync(tc, agent.Id, channelId, ct);
                if (inlineHandled)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, inlineResult ?? ""));
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

                messages.Add(BuildToolResultMessage(tc.Id, jobResponse, supportsVision));
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

        yield return ChatStreamEvent.Complete(new ChatResponse(
            ToMessageResponse(userMessage),
            ToMessageResponse(assistantMessage),
            jobResults.Count > 0 ? jobResults : null,
            channelCost,
            threadCost));

        } // try
        finally
        {
            threadLock?.Dispose();
            if (isLocal)
                localModelService.ReleaseAfterChat(model.Id);
        }
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

    // ═══════════════════════════════════════════════════════════════
    // Task-specific tool handling
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the effective tool list for a chat call.  When a task
    /// context is present, task-specific tools (shared data, output,
    /// introspection, custom hooks) are appended to the standard set.
    /// When a <paramref name="toolAwareness"/> filter is provided, only
    /// tools whose key is <see langword="true"/> or absent are kept.
    /// </summary>
    private static IReadOnlyList<ChatToolDefinition> GetEffectiveTools(
        TaskChatContext? taskContext, Dictionary<string, bool>? toolAwareness = null)
    {
        IReadOnlyList<ChatToolDefinition> baseTools;

        if (taskContext is null)
        {
            baseTools = AllTools;
        }
        else
        {
            var store = TaskSharedData.Get(taskContext.InstanceId);
            if (store is null)
            {
                baseTools = AllTools;
            }
            else
            {
                var tools = new List<ChatToolDefinition>(AllTools);
                tools.AddRange(BuiltInTaskTools);

                // task_output only available when the task declares [AgentOutput]
                if (store.AllowedOutputFormat is not null)
                    tools.Add(TaskOutputToolDef);

                // Custom [ToolCall] hooks
                tools.AddRange(store.CustomToolDefinitions);

                baseTools = tools;
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
    /// Try to handle a tool call as a permission-free inline tool (e.g. <c>wait</c>)
    /// or a cross-thread context tool (e.g. <c>list_accessible_threads</c>,
    /// <c>read_thread_history</c>).
    /// Returns <c>true</c> and sets <paramref name="result"/> if handled.
    /// These tools never enter the job/permission pipeline.
    /// </summary>
    private async Task<(bool Handled, string? Result)> TryHandleInlineToolAsync(
        ChatToolCall toolCall, Guid agentId, Guid channelId, CancellationToken ct)
    {
        switch (toolCall.Name)
        {
            case "wait":
                return await HandleWaitToolAsync(toolCall, ct);
            case "list_accessible_threads":
                return await HandleListAccessibleThreadsAsync(agentId, channelId, ct);
            case "read_thread_history":
                return await HandleReadThreadHistoryAsync(toolCall, agentId, channelId, ct);
            default:
                return (false, null);
        }
    }

    private static async Task<(bool, string?)> HandleWaitToolAsync(
        ChatToolCall toolCall, CancellationToken ct)
    {
        try
        {
            var seconds = 5; // default

            if (!string.IsNullOrEmpty(toolCall.ArgumentsJson))
            {
                using var doc = JsonDocument.Parse(toolCall.ArgumentsJson);
                if (doc.RootElement.TryGetProperty("seconds", out var secEl))
                    seconds = secEl.GetInt32();
            }

            seconds = Math.Clamp(seconds, 1, 300);

            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);

            return (true, $"Waited {seconds} second{(seconds == 1 ? "" : "s")}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (true, $"Error in wait tool: {ex.Message}");
        }
    }

    private async Task<(bool, string?)> HandleListAccessibleThreadsAsync(
        Guid agentId, Guid channelId, CancellationToken ct)
    {
        try
        {
            var threads = await GetAccessibleThreadsAsync(agentId, channelId, ct);
            if (threads.Count == 0)
                return (true, "No accessible threads found. Either the agent lacks the ReadCrossThreadHistory permission, or no other channels have opted in.");

            var result = threads.Select(t => new
            {
                threadId = t.ThreadId.ToString("D"),
                threadName = t.ThreadName,
                channelId = t.ChannelId.ToString("D"),
                channelTitle = t.ChannelTitle,
            });

            return (true, JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            return (true, $"Error listing accessible threads: {ex.Message}");
        }
    }

    private async Task<(bool, string?)> HandleReadThreadHistoryAsync(
        ChatToolCall toolCall, Guid agentId, Guid channelId, CancellationToken ct)
    {
        try
        {
            Guid threadId = Guid.Empty;
            int maxMessages = 50;

            if (!string.IsNullOrEmpty(toolCall.ArgumentsJson))
            {
                using var doc = JsonDocument.Parse(toolCall.ArgumentsJson);
                if (doc.RootElement.TryGetProperty("threadId", out var tidEl))
                    Guid.TryParse(tidEl.GetString(), out threadId);
                if (doc.RootElement.TryGetProperty("maxMessages", out var maxEl))
                    maxMessages = Math.Clamp(maxEl.GetInt32(), 1, 200);
            }

            if (threadId == Guid.Empty)
                return (true, "Error: threadId is required.");

            // Load thread with its channel
            var thread = await db.ChatThreads
                .Include(t => t.Channel)
                    .ThenInclude(c => c.AllowedAgents)
                .Include(t => t.Channel)
                    .ThenInclude(c => c.PermissionSet)
                .Include(t => t.Channel)
                    .ThenInclude(c => c.AgentContext)
                        .ThenInclude(ctx => ctx!.PermissionSet)
                .FirstOrDefaultAsync(t => t.Id == threadId, ct);

            if (thread is null)
                return (true, "Error: thread not found.");

            // Must not be the current channel (use normal history for that)
            if (thread.ChannelId == channelId)
                return (true, "Error: use normal chat history to access threads in the current channel.");

            // Check agent has access to the target channel
            var targetChannel = thread.Channel;
            var isAgentOnChannel = targetChannel.AgentId == agentId
                || targetChannel.AllowedAgents.Any(a => a.Id == agentId);
            if (!isAgentOnChannel)
                return (true, "Error: agent is not assigned to the target channel.");

            // Check agent has ReadCrossThreadHistory permission
            var agentWithRole = await db.Agents
                .Include(a => a.Role)
                    .ThenInclude(r => r!.PermissionSet)
                .FirstOrDefaultAsync(a => a.Id == agentId, ct);

            var agentPs = agentWithRole?.Role?.PermissionSet;
            if (agentPs is not { CanReadCrossThreadHistory: true })
                return (true, "Error: agent lacks ReadCrossThreadHistory permission.");

            // Check channel opt-in (unless Independent clearance)
            if (agentPs.ReadCrossThreadHistoryClearance != PermissionClearance.Independent)
            {
                var effectivePs = targetChannel.PermissionSet
                    ?? targetChannel.AgentContext?.PermissionSet;
                if (effectivePs?.CanReadCrossThreadHistory != true)
                    return (true, "Error: the target channel has not opted in to cross-thread history sharing.");
            }

            // Fetch messages
            var messages = await db.ChatMessages
                .Where(m => m.ThreadId == threadId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(maxMessages)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new
                {
                    role = m.Role,
                    content = m.Content,
                    sender = m.SenderUsername ?? m.SenderAgentName ?? "unknown",
                    timestamp = m.CreatedAt
                })
                .ToListAsync(ct);

            if (messages.Count == 0)
                return (true, "Thread exists but has no messages.");

            return (true, JsonSerializer.Serialize(messages));
        }
        catch (Exception ex)
        {
            return (true, $"Error reading thread history: {ex.Message}");
        }
    }


    /// <summary>
    /// Finds threads accessible to this agent via cross-thread history
    /// sharing.  A thread is accessible when:
    /// <list type="number">
    ///   <item>The agent has <c>CanReadCrossThreadHistory</c>.</item>
    ///   <item>The agent is primary or allowed on the thread's channel.</item>
    ///   <item>The channel's effective permission set also has
    ///         <c>CanReadCrossThreadHistory</c> — unless the agent has
    ///         <c>Independent</c> clearance for the flag.</item>
    /// </list>
    /// The current channel is excluded (the agent already has its own
    /// history there).
    /// </summary>
    private async Task<List<(Guid ThreadId, string ThreadName, Guid ChannelId, string ChannelTitle)>>
        GetAccessibleThreadsAsync(Guid agentId, Guid currentChannelId, CancellationToken ct)
    {
        var agentWithRole = await db.Agents
            .Include(a => a.Role)
                .ThenInclude(r => r!.PermissionSet)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        if (agentWithRole?.Role?.PermissionSet is not { CanReadCrossThreadHistory: true } agentPs)
            return [];

        var isIndependent = agentPs.ReadCrossThreadHistoryClearance == PermissionClearance.Independent;

        // Channels where the agent is primary or allowed, excluding current
        var channels = await db.Channels
            .Include(c => c.AllowedAgents)
            .Include(c => c.PermissionSet)
            .Include(c => c.AgentContext)
                .ThenInclude(ctx => ctx!.PermissionSet)
            .Where(c => c.Id != currentChannelId)
            .Where(c => c.AgentId == agentId || c.AllowedAgents.Any(a => a.Id == agentId))
            .ToListAsync(ct);

        // Filter by channel opt-in unless Independent
        if (!isIndependent)
        {
            channels = channels
                .Where(c =>
                {
                    var effectivePs = c.PermissionSet ?? c.AgentContext?.PermissionSet;
                    return effectivePs?.CanReadCrossThreadHistory == true;
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
        ModelCapability modelCapabilities,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
        CancellationToken ct,
        TaskChatContext? taskContext = null,
        Dictionary<string, bool>? toolAwareness = null)
    {
        var messages = new List<ToolAwareMessage>(dbHistory.Count);
        foreach (var msg in dbHistory)
            messages.Add(new ToolAwareMessage { Role = msg.Role, Content = msg.Content });

        var supportsVision = modelCapabilities.HasFlag(ModelCapability.Vision);
        var jobResults = new List<AgentJobResponse>();
        var rounds = 0;
        var effectiveTools = GetEffectiveTools(taskContext, toolAwareness);
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;

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
                return new ToolLoopResult(result.Content ?? "", jobResults, totalPromptTokens, totalCompletionTokens);

            // Record assistant turn with tool calls
            messages.Add(ToolAwareMessage.AssistantWithToolCalls(result.ToolCalls, result.Content));

            var anyUnresolvableApproval = false;

            foreach (var tc in result.ToolCalls)
            {
                // ── Task-specific tool interception ──────────────
                var (handled, taskResult) = await TryHandleTaskToolAsync(tc, taskContext, ct);
                if (handled)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, taskResult ?? ""));
                    continue;
                }

                // ── Inline tool interception (no permissions) ────
                var (inlineHandled, inlineResult) = await TryHandleInlineToolAsync(tc, agentId, channelId, ct);
                if (inlineHandled)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, inlineResult ?? ""));
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

                messages.Add(BuildToolResultMessage(tc.Id, jobResponse, supportsVision));

                if (jobResponse.Status == AgentJobStatus.AwaitingApproval)
                    anyUnresolvableApproval = true;
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
                return new ToolLoopResult(finalResult.Content ?? "", jobResults, totalPromptTokens, totalCompletionTokens);
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
        ModelCapability modelCapabilities,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
        CancellationToken ct,
        TaskChatContext? taskContext = null)
    {
        var supportsVision = modelCapabilities.HasFlag(ModelCapability.Vision);
        var jobResults = new List<AgentJobResponse>();
        string assistantContent;
        var rounds = 0;
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;

        while (true)
        {
            var completionResult = await client.ChatCompletionAsync(
                httpClient, apiKey, modelName, systemPrompt, history, maxCompletionTokens, providerParameters, completionParameters, ct);

            assistantContent = completionResult.Content ?? "";
            if (completionResult.Usage is { } usage)
            {
                totalPromptTokens += usage.PromptTokens;
                totalCompletionTokens += usage.CompletionTokens;
            }

            var toolCalls = ParseToolCalls(assistantContent);
            if (toolCalls.Count == 0 || ++rounds > MaxToolCallRounds)
                break;

            history.Add(new ChatCompletionMessage("assistant", assistantContent));

            var toolResultBuilder = new StringBuilder();
            var anyUnresolvableApproval = false;

            foreach (var call in toolCalls)
            {
                // ── Task-specific tool interception (text-based) ──
                var syntheticTc = new ChatToolCall(call.CallId, call.CallId, call.RawJson ?? call.ScriptJson ?? "{}");
                var (handled, taskResult) = await TryHandleTaskToolAsync(syntheticTc, taskContext, ct);
                if (handled)
                {
                    toolResultBuilder.AppendLine(
                        $"[TOOL_RESULT:{call.CallId}] status=Completed result={taskResult}");
                    continue;
                }

                // ── Inline tool interception (no permissions) ────
                var (inlineHandled, inlineResult) = await TryHandleInlineToolAsync(syntheticTc, agentId, channelId, ct);
                if (inlineHandled)
                {
                    toolResultBuilder.AppendLine(
                        $"[TOOL_RESULT:{call.CallId}] status=Completed result={inlineResult}");
                    continue;
                }

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

                // For the text-based loop, screenshots are stripped from the
                // result text because the simple ChatCompletionMessage doesn't
                // support multipart content in the same way.
                var (textResult, _) = ExtractScreenshotData(jobResponse);

                toolResultBuilder.AppendLine(
                    $"[TOOL_RESULT:{call.CallId}] status={jobResponse.Status}" +
                    (textResult is not null ? $" result={textResult}" : "") +
                    (jobResponse.ErrorLog is not null ? $" error={jobResponse.ErrorLog}" : ""));

                if (jobResponse.Status == AgentJobStatus.AwaitingApproval)
                    anyUnresolvableApproval = true;
            }

            history.Add(new ChatCompletionMessage("user", toolResultBuilder.ToString()));

            if (anyUnresolvableApproval)
            {
                var finalResult = await client.ChatCompletionAsync(
                    httpClient, apiKey, modelName, systemPrompt, history, maxCompletionTokens, providerParameters, completionParameters, ct);
                assistantContent = finalResult.Content ?? "";
                if (finalResult.Usage is { } finalUsage)
                {
                    totalPromptTokens += finalUsage.PromptTokens;
                    totalCompletionTokens += finalUsage.CompletionTokens;
                }
                break;
            }
        }

        assistantContent = StripToolCallBlocks(assistantContent);
        return new ToolLoopResult(assistantContent, jobResults, totalPromptTokens, totalCompletionTokens);
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
            Debug.WriteLine(
                $"[ParseToolCall] {toolCall.Name} (id={toolCall.Id}) args: {toolCall.ArgumentsJson}",
                "SharpClaw.CLI");

            var payload = JsonSerializer.Deserialize<ToolCallPayload>(toolCall.ArgumentsJson, JsonOptions);
            if (payload is null) return null;

            Guid? resourceId = Guid.TryParse(payload.ResourceId, out var rid) ? rid : null;
            // TargetId is the generic "resourceId" alias for non-shell tools
            resourceId ??= Guid.TryParse(payload.TargetId, out var tid) ? tid : null;

            Debug.WriteLine(
                $"[ParseToolCall] {toolCall.Name} → resourceId={resourceId}, targetId={payload.TargetId}",
                "SharpClaw.CLI");

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
        ["capture_display"]                = AgentActionType.CaptureDisplay,
        ["click_desktop"]                  = AgentActionType.ClickDesktop,
        ["type_on_desktop"]                = AgentActionType.TypeOnDesktop,
        ["editor_read_file"]               = AgentActionType.EditorReadFile,
        ["editor_get_open_files"]          = AgentActionType.EditorGetOpenFiles,
        ["editor_get_selection"]            = AgentActionType.EditorGetSelection,
        ["editor_get_diagnostics"]         = AgentActionType.EditorGetDiagnostics,
        ["editor_apply_edit"]              = AgentActionType.EditorApplyEdit,
        ["editor_create_file"]             = AgentActionType.EditorCreateFile,
        ["editor_delete_file"]             = AgentActionType.EditorDeleteFile,
        ["editor_show_diff"]               = AgentActionType.EditorShowDiff,
        ["editor_run_build"]               = AgentActionType.EditorRunBuild,
        ["editor_run_terminal"]            = AgentActionType.EditorRunTerminal,
    };

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
                toolCallId, resultContent, imageBase64, "image/jpeg");
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
                        payload.WorkingDirectory,
                        json));
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
        var clickDesktopSchema = BuildClickDesktopSchema();
        var typeOnDesktopSchema = BuildTypeOnDesktopSchema();
        var editorReadFileSchema = BuildEditorReadFileSchema();
        var editorFileOptionalSchema = BuildEditorFileOptionalSchema();
        var editorFileRequiredSchema = BuildEditorFileRequiredSchema();
        var editorApplyEditSchema = BuildEditorApplyEditSchema();
        var editorCreateFileSchema = BuildEditorCreateFileSchema();
        var editorShowDiffSchema = BuildEditorShowDiffSchema();
        var editorRunTerminalSchema = BuildEditorRunTerminalSchema();
        var waitSchema = BuildWaitSchema();
        var readThreadHistorySchema = BuildReadThreadHistorySchema();

        return
        [
            // ── Inline tools (no permissions) ─────────────────
            new("wait",
                "Pause for 1–300 seconds. No tokens consumed while waiting.",
                waitSchema),

            // ── Cross-thread context tools ────────────────────
            new("list_accessible_threads",
                "List readable threads from other channels (IDs, names, parent channel).",
                globalSchema),
            new("read_thread_history",
                "Read history from a cross-channel thread. Provide threadId; optional maxMessages (1–200, default 50).",
                readThreadHistorySchema),

            // ── Shell execution ───────────────────────────────
            new("execute_mk8_shell", Mk8ShellToolDescription, mk8Schema),
            new("execute_dangerous_shell",
                "Execute a raw shell command (Bash/PowerShell/Cmd/Git). No sandboxing. Optional workingDirectory overrides default CWD.",
                dangerousShellSchema),

            // ── Transcription ────────────────────────────────
            new("transcribe_from_audio_device",
                "Start live transcription from a system audio device.",
                transcriptionSchema),
            new("transcribe_from_audio_stream",
                "Transcribe an incoming audio stream. [Stub — not yet implemented.]",
                transcriptionSchema),
            new("transcribe_from_audio_file",
                "Transcribe a pre-recorded audio file. [Stub — not yet implemented.]",
                transcriptionSchema),

            // ── Global flags ─────────────────────────────────────
            new("create_sub_agent",
                "Create a sub-agent. Provide name, modelId, and optional systemPrompt.",
                createSubAgentSchema),
            new("create_container",
                "Create an mk8.shell sandbox container. Name: alphanumeric only.",
                createContainerSchema),
            new("register_info_store",
                "Register an information store. [Stub — not yet implemented.]",
                globalSchema),
            new("access_localhost_in_browser",
                "Headless-browser GET on a localhost URL. mode='html' (default) returns DOM; 'screenshot' returns PNG (vision only). localhost/127.0.0.1 only.",
                localhostBrowserSchema),
            new("access_localhost_cli",
                "HTTP GET a localhost URL; returns status, headers, body. localhost/127.0.0.1 only.",
                localhostCliSchema),

            // ── Per-resource ─────────────────────────────────────
            new("access_local_info_store",
                "Query a local information store. [Stub.]",
                resourceOnly),
            new("access_external_info_store",
                "Query an external information store. [Stub.]",
                resourceOnly),
            new("access_website",
                "Access a registered website. [Stub.]",
                resourceOnly),
            new("query_search_engine",
                "Query a registered search engine. [Stub.]",
                resourceOnly),
            new("access_container",
                "Access a container resource. [Stub.]",
                resourceOnly),
            new("manage_agent",
                "Update an agent's name, systemPrompt, or modelId.",
                manageAgentSchema),
            new("edit_task",
                "Edit a task's name, interval, or retry settings.",
                editTaskSchema),
            new("access_skill",
                "Retrieve a skill's instruction text.",
                resourceOnly),
            new("capture_display",
                "Screenshot a display. Returns base64 PNG (vision) or text description.",
                resourceOnly),
            new("click_desktop",
                "Click at (x,y) on a display. Returns follow-up screenshot.",
                clickDesktopSchema),
            new("type_on_desktop",
                "Type text via keyboard. Optional (x,y) click to focus first. Returns follow-up screenshot.",
                typeOnDesktopSchema),

            // ── Editor actions ────────────────────────────────────
            new("editor_read_file",
                "Read a file from the IDE. Optional startLine/endLine for a range.",
                editorReadFileSchema),
            new("editor_get_open_files",
                "List open files/tabs in the IDE.",
                resourceOnly),
            new("editor_get_selection",
                "Get active file, cursor position, and selected text.",
                resourceOnly),
            new("editor_get_diagnostics",
                "Get compilation errors/warnings. Optional filePath to scope.",
                editorFileOptionalSchema),
            new("editor_apply_edit",
                "Replace a line range in a file with newText.",
                editorApplyEditSchema),
            new("editor_create_file",
                "Create a file in the IDE workspace.",
                editorCreateFileSchema),
            new("editor_delete_file",
                "Delete a file from the IDE workspace.",
                editorFileRequiredSchema),
            new("editor_show_diff",
                "Show a diff view in the IDE. User can accept/reject.",
                editorShowDiffSchema),
            new("editor_run_build",
                "Trigger a build and return output/errors.",
                resourceOnly),
            new("editor_run_terminal",
                "Run a command in the IDE terminal.",
                editorRunTerminalSchema),
        ];
    }

    private static JsonElement BuildWaitSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "seconds": {
                        "type": "integer",
                        "description": "Seconds (1–300)."
                    }
                },
                "required": ["seconds"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildReadThreadHistorySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "threadId": {
                        "type": "string",
                        "description": "Thread GUID (from list_accessible_threads)."
                    },
                    "maxMessages": {
                        "type": "integer",
                        "description": "Max messages (1–200, default 50)."
                    }
                },
                "required": ["threadId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildMk8ShellToolSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resourceId": {
                        "type": "string",
                        "description": "Container GUID."
                    },
                    "sandboxId": {
                        "type": "string",
                        "description": "Sandbox name."
                    },
                    "script": {
                        "type": "object",
                        "description": "Script object.",
                        "properties": {
                            "operations": {
                                "type": "array",
                                "description": "Ordered operations.",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "verb": { "type": "string" },
                                        "args": { "type": "array", "items": { "type": "string" } },
                                        "workingDirectory": {
                                            "type": "string",
                                            "description": "Per-step CWD override (e.g. '$WORKSPACE/subdir')."
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
                                            "description": "Per-step CWD override."
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
                        "description": "Resource GUID."
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
                        "description": "SystemUser GUID."
                    },
                    "shellType": {
                        "type": "string",
                        "enum": ["Bash", "PowerShell", "CommandPrompt", "Git"],
                        "description": "Shell interpreter."
                    },
                    "command": {
                        "type": "string",
                        "description": "Raw command string."
                    },
                    "workingDirectory": {
                        "type": "string",
                        "description": "Optional CWD override."
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
                        "description": "Audio device GUID."
                    },
                    "transcriptionModelId": {
                        "type": "string",
                        "description": "Transcription model GUID."
                    },
                    "language": {
                        "type": "string",
                        "description": "BCP-47 code (e.g. 'en')."
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
                        "description": "Agent name."
                    },
                    "modelId": {
                        "type": "string",
                        "description": "Model GUID."
                    },
                    "systemPrompt": {
                        "type": "string",
                        "description": "System prompt."
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
                        "description": "Name (alphanumeric)."
                    },
                    "path": {
                        "type": "string",
                        "description": "Absolute parent directory."
                    },
                    "description": {
                        "type": "string",
                        "description": "Description."
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
                        "description": "Agent GUID."
                    },
                    "name": {
                        "type": "string",
                        "description": "New name."
                    },
                    "systemPrompt": {
                        "type": "string",
                        "description": "New system prompt."
                    },
                    "modelId": {
                        "type": "string",
                        "description": "New model GUID."
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
                        "description": "Task GUID."
                    },
                    "name": {
                        "type": "string",
                        "description": "New name."
                    },
                    "repeatIntervalMinutes": {
                        "type": "integer",
                        "description": "Minutes. 0=remove."
                    },
                    "maxRetries": {
                        "type": "integer",
                        "description": "Max retries."
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
                        "description": "Localhost URL."
                    },
                    "mode": {
                        "type": "string",
                        "enum": ["html", "screenshot"],
                        "description": "'html' (default)=DOM, 'screenshot'=PNG."
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
                        "description": "Localhost URL."
                    }
                },
                "required": ["url"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildClickDesktopSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Display GUID."
                    },
                    "x": {
                        "type": "integer",
                        "description": "X coordinate."
                    },
                    "y": {
                        "type": "integer",
                        "description": "Y coordinate."
                    },
                    "button": {
                        "type": "string",
                        "enum": ["left", "right", "middle"],
                        "description": "Default 'left'."
                    },
                    "clickType": {
                        "type": "string",
                        "enum": ["single", "double"],
                        "description": "Default 'single'."
                    }
                },
                "required": ["targetId", "x", "y"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildTypeOnDesktopSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Display GUID."
                    },
                    "text": {
                        "type": "string",
                        "description": "Text to type."
                    },
                    "x": {
                        "type": "integer",
                        "description": "X click-to-focus."
                    },
                    "y": {
                        "type": "integer",
                        "description": "Y click-to-focus."
                    }
                },
                "required": ["targetId", "text"]
            }
            """);
        return doc.RootElement.Clone();
    }

    // ── Editor action schemas ─────────────────────────────────────

    private static JsonElement BuildEditorReadFileSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": { "type": "string", "description": "EditorSession GUID." },
                    "filePath": { "type": "string", "description": "File path relative to workspace root." },
                    "startLine": { "type": "integer", "description": "Optional start line (1-based)." },
                    "endLine": { "type": "integer", "description": "Optional end line (1-based, inclusive)." }
                },
                "required": ["targetId", "filePath"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildEditorFileOptionalSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": { "type": "string", "description": "EditorSession GUID." },
                    "filePath": { "type": "string", "description": "Optional file path to scope results." }
                },
                "required": ["targetId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildEditorFileRequiredSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": { "type": "string", "description": "EditorSession GUID." },
                    "filePath": { "type": "string", "description": "File path relative to workspace root." }
                },
                "required": ["targetId", "filePath"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildEditorApplyEditSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": { "type": "string", "description": "EditorSession GUID." },
                    "filePath": { "type": "string", "description": "File path relative to workspace root." },
                    "startLine": { "type": "integer", "description": "Start line (1-based)." },
                    "endLine": { "type": "integer", "description": "End line (1-based, inclusive)." },
                    "newText": { "type": "string", "description": "Replacement text." }
                },
                "required": ["targetId", "filePath", "startLine", "endLine", "newText"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildEditorCreateFileSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": { "type": "string", "description": "EditorSession GUID." },
                    "filePath": { "type": "string", "description": "File path relative to workspace root." },
                    "content": { "type": "string", "description": "Initial file content." }
                },
                "required": ["targetId", "filePath"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildEditorShowDiffSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": { "type": "string", "description": "EditorSession GUID." },
                    "filePath": { "type": "string", "description": "File path relative to workspace root." },
                    "proposedContent": { "type": "string", "description": "Proposed file content." },
                    "diffTitle": { "type": "string", "description": "Diff view title." }
                },
                "required": ["targetId", "filePath", "proposedContent"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildEditorRunTerminalSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": { "type": "string", "description": "EditorSession GUID." },
                    "command": { "type": "string", "description": "Command to run." },
                    "workingDirectory": { "type": "string", "description": "Working directory." }
                },
                "required": ["targetId", "command"]
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
        string? WorkingDirectory = null,
        string? RawJson = null);

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
        List<AgentJobResponse> JobResults,
        int TotalPromptTokens = 0,
        int TotalCompletionTokens = 0);

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
