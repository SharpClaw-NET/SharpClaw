using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.Modules;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Conversation;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Application.Services;

public sealed class ChatService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    IPersistenceEntityResolver entities,
    IHttpClientFactory httpClientFactory,
    AgentJobService jobService,
    HeaderTagProcessor headerTagProcessor,
    ThreadActivitySignal threadActivity,
    ModuleRegistry moduleRegistry,
    ModuleToolExecutionPlanner moduleExecutionPlanner,
    ChatCache chatCache,
    ChatCostEngine chatCosts,
    ChatRequestPlanningEngine chatPlanner,
    ChatHistoryEngine chatHistory,
    ChatDefaultHeaderEngine chatHeaders,
    ChatHeaderGrantFormatter headerGrantFormatter,
    ChatToolResultEngine chatToolResults,
    ChatMessageEngine chatMessages,
    ChatToolSelectionEngine chatToolSelection,
    ChatNativeJobToolExecutor chatNativeJobToolExecutor,
    ChatInlineToolExecutor chatInlineToolExecutor,
    ConversationTopologyEngine conversation,
    ILogger<ChatService> logger,
    IServiceScopeFactory serviceScopeFactory,
    IServiceProvider serviceProvider,
    IConfiguration configuration)
{
    /// <summary>
    /// Maximum number of tool-call round-trips before forcing a final
    /// response.  Prevents infinite loops when the model keeps emitting
    /// tool calls.
    /// </summary>
    private const int MaxToolCallRounds = 50;

    private readonly bool _disableCustomProviderParameters =
        configuration.GetValue<bool>("Agent:DisableCustomProviderParameters");

    private readonly bool _disableDefaultChatHeaders =
        configuration.GetValue<bool>("Chat:DisableDefaultHeaders");

    private readonly bool _disableDefaultSystemPrompt =
        configuration.GetValue<bool>("Chat:DisableDefaultSystemPrompt");

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
        var timingRequestId = Guid.NewGuid().ToString("N")[..8];
        var totalTiming = Stopwatch.StartNew();
        var logTiming = logger.IsEnabled(LogLevel.Debug);
        bool userMessagePersisted = false;

        if (logTiming)
        {
            logger.LogDebug(
                "Chat request {RequestId} started. ChannelId={ChannelId} ThreadId={ThreadId} RequestedAgentId={RequestedAgentId} ClientType={ClientType} MessageChars={MessageChars} CancellationRequested={CancellationRequested}",
                timingRequestId, channelId, threadId, request.AgentId,
                PathGuard.SanitizeForLog(request.ClientType),
                request.Message.Length, ct.IsCancellationRequested);
        }

        var channel = await db.Channels
            .AsNoTracking()
            .Include(c => c.Agent!).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent!).ThenInclude(a => a.ToolAwarenessSet)
            .Include(c => c.Agent!).ThenInclude(a => a.Role)
            .Include(c => c.ToolAwarenessSet)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.ToolAwarenessSet)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Role)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == channelId, ct)
            ?? throw new ArgumentException($"Channel {channelId} not found.");

        var agent = conversation.ResolveRequestedAgent(channel, request.AgentId);
        var plan = chatPlanner.BuildBufferedPlan(
            channel,
            agent,
            threadId,
            _disableDefaultSystemPrompt,
            _disableCustomProviderParameters);
        var model = agent.Model!;
        var provider = model.Provider!;

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
        var historyTiming = Stopwatch.StartNew();
        List<ChatCompletionMessage> history;
        int? maxHistoryMessages = null;
        int? maxHistoryCharacters = null;
        if (threadId is not null)
        {
            var historyLoad = await LoadThreadHistoryAsync(threadId.Value, ct);
            history = historyLoad.Messages;
            maxHistoryMessages = historyLoad.MaxMessages;
            maxHistoryCharacters = historyLoad.MaxCharacters;
        }
        else
        {
            history = [];
        }
        historyTiming.Stop();

        if (logTiming)
        {
            logger.LogDebug(
                "Chat request {RequestId} loaded history in {HistoryLoadMs}ms. ThreadId={ThreadId} HistoryMessages={HistoryMessages} HistoryChars={HistoryChars} MaxHistoryMessages={MaxHistoryMessages} MaxHistoryCharacters={MaxHistoryCharacters} ElapsedMs={ElapsedMs}",
                timingRequestId, historyTiming.ElapsedMilliseconds, threadId,
                history.Count, history.Sum(m => m.Content.Length),
                maxHistoryMessages, maxHistoryCharacters,
                totalTiming.ElapsedMilliseconds);
        }

        history.Add(new ChatCompletionMessage(ChatRoles.User, request.Message));

        var apiKey = plan.RequiresApiKey
            ? ApiKeyEncryptor.DecryptOrPassthrough(provider.EncryptedApiKey!, encryptionOptions.Key)
            : "local";
        var client = plan.Client;
        var useNativeTools = plan.UseNativeTools;
        var enableTools = plan.EnableTools;
        var systemPrompt = plan.SystemPrompt;
        var completionParams = plan.CompletionParameters;

        // Build chat header for the user message (if enabled)
        var chatHeader = await BuildChatHeaderAsync(channel, agent, request.ClientType, ct,
            taskContext: request.TaskContext, externalUsername: request.ExternalUsername, externalDisplayName: request.ExternalDisplayName,
            completionParameters: completionParams, providerKey: provider.ProviderKey);
        var messageForModel = chatHeader is not null
            ? chatHeader + request.Message
            : request.Message;

        // Replace last history entry with the header-prefixed version for model
        history[^1] = new ChatCompletionMessage(ChatRoles.User, messageForModel);

        using var httpClient = httpClientFactory.CreateClient();

        var modelCapabilityTags = plan.ModelCapabilityTags;
        var maxTokens = plan.MaxCompletionTokens;
        var providerParams = plan.ProviderParameters;
        var toolAwareness = plan.ToolAwareness;

        if (logTiming)
        {
            logger.LogDebug(
                "Chat request {RequestId} prepared provider call. AgentId={AgentId} AgentName={AgentName} ModelId={ModelId} ModelName={ModelName} ProviderKey={ProviderKey} ProviderName={ProviderName} SystemPromptChars={SystemPromptChars} UseNativeTools={UseNativeTools} EnableTools={EnableTools} ToolAwarenessCount={ToolAwarenessCount} MaxCompletionTokens={MaxCompletionTokens} ProviderParametersPresent={ProviderParametersPresent} CompletionParametersPresent={CompletionParametersPresent} ElapsedMs={ElapsedMs}",
                timingRequestId, agent.Id, PathGuard.SanitizeForLog(agent.Name),
                model.Id, PathGuard.SanitizeForLog(model.Name),
                PathGuard.SanitizeForLog(provider.ProviderKey),
                PathGuard.SanitizeForLog(provider.Name),
                systemPrompt.Length, useNativeTools, enableTools,
                toolAwareness?.Count ?? 0, maxTokens,
                providerParams is not null, completionParams is not null,
                totalTiming.ElapsedMilliseconds);
        }

        // Persist user message immediately so it gets an accurate
        // CreatedAt and survives crashes during LLM inference.
        var senderUserId = jobService.GetSessionUserId();
        var senderUserSnapshot = await ResolveUserSenderSnapshotAsync(
            senderUserId, request.ExternalDisplayName, request.ExternalUsername, ct);

        var userMessage = chatMessages.CreateUserMessage(
            channelId,
            threadId,
            request,
            senderUserId,
            senderUserSnapshot.Username,
            senderUserSnapshot.RoleId,
            senderUserSnapshot.RoleName);

        db.ChatMessages.Add(userMessage);
        await db.SaveChangesAsync(CancellationToken.None);
        userMessagePersisted = true;

        var providerTiming = Stopwatch.StartNew();
        var loopResult = enableTools
            ? await RunNativeToolLoopAsync(
                client, httpClient, apiKey, model.Name, systemPrompt,
                history, agent.Id, channelId, modelCapabilityTags, maxTokens, providerParams, completionParams, approvalCallback, ct,
                taskContext: request.TaskContext, toolAwareness: toolAwareness, threadId: threadId,
                timingRequestId: timingRequestId, totalTiming: totalTiming)
            : await RunPlainCompletionAsync(
                client, httpClient, apiKey, model.Name, systemPrompt,
                history, maxTokens, providerParams, completionParams, ct);
        providerTiming.Stop();

        if (logTiming)
        {
            logger.LogDebug(
                "Chat request {RequestId} provider call completed in {ProviderCallMs}ms. PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} TotalTokens={TotalTokens} JobResults={JobResultCount} AssistantContentChars={AssistantContentChars} CancellationRequested={CancellationRequested} ElapsedMs={ElapsedMs}",
                timingRequestId, providerTiming.ElapsedMilliseconds,
                loopResult.TotalPromptTokens, loopResult.TotalCompletionTokens,
                loopResult.TotalPromptTokens + loopResult.TotalCompletionTokens,
                loopResult.JobResults.Count, loopResult.AssistantContent.Length,
                ct.IsCancellationRequested, totalTiming.ElapsedMilliseconds);
        }

        // Persist assistant message after LLM completes
        var assistantMessage = chatMessages.CreateAssistantMessage(
            channelId,
            threadId,
            request,
            agent,
            loopResult.AssistantContent,
            loopResult.TotalPromptTokens,
            loopResult.TotalCompletionTokens,
            loopResult.ProviderMetadataJson);

        db.ChatMessages.Add(assistantMessage);
        var assistantSaveTiming = Stopwatch.StartNew();
        await db.SaveChangesAsync(CancellationToken.None);
        assistantSaveTiming.Stop();

        await jobService.RecordTokensForCurrentExecutionAsync(
            loopResult.TotalPromptTokens, loopResult.TotalCompletionTokens, ct);
        chatCache.RecordAssistantTokens(
            channelId,
            threadId,
            agent.Id,
            agent.Name,
            loopResult.TotalPromptTokens,
            loopResult.TotalCompletionTokens);

        if (logTiming)
        {
            logger.LogDebug(
                "Chat request {RequestId} saved assistant message in {AssistantSaveMs}ms. AssistantMessageId={AssistantMessageId} ElapsedMs={ElapsedMs}",
                timingRequestId, assistantSaveTiming.ElapsedMilliseconds,
                assistantMessage.Id, totalTiming.ElapsedMilliseconds);
        }

        if (threadId is not null)
            threadActivity.Publish(threadId.Value,
                new ThreadActivityEvent(ThreadActivityEventType.NewMessages, request.ClientType));

        // Piggyback cost data on the response so callers don't need
        // a separate round-trip to the /cost endpoints.
        var costTiming = Stopwatch.StartNew();
        var (channelCost, threadCost, agentCost) =
            await GetResponseCostsAsync(channelId, threadId, agent.Id, agent.Name, ct);
        costTiming.Stop();

        if (logTiming)
        {
            logger.LogDebug(
                "Chat request {RequestId} completed in {ElapsedMs}ms. CostLoadMs={CostLoadMs} ChannelTokens={ChannelTokens} ThreadTokens={ThreadTokens} AgentTokens={AgentTokens}",
                timingRequestId, totalTiming.ElapsedMilliseconds,
                costTiming.ElapsedMilliseconds, channelCost.TotalTokens,
                threadCost?.TotalTokens, agentCost?.TotalTokens);
        }

        return new ChatResponse(
            chatMessages.ToResponse(userMessage),
            chatMessages.ToResponse(assistantMessage),
            loopResult.JobResults.Count > 0 ? loopResult.JobResults : null,
            channelCost,
            threadCost,
            agentCost);

        }
        catch (OperationCanceledException ex)
        {
            if (logTiming)
            {
                logger.LogDebug(
                    ex,
                    "Chat request {RequestId} cancelled after {ElapsedMs}ms. ChannelId={ChannelId} ThreadId={ThreadId} UserMessagePersisted={UserMessagePersisted} CancellationRequested={CancellationRequested}",
                    timingRequestId, totalTiming.ElapsedMilliseconds,
                    channelId, threadId, userMessagePersisted,
                    ct.IsCancellationRequested);
            }

            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Chat request {RequestId} failed after {ElapsedMs}ms. ChannelId={ChannelId} ThreadId={ThreadId} UserMessagePersisted={UserMessagePersisted} CancellationRequested={CancellationRequested}",
                timingRequestId, totalTiming.ElapsedMilliseconds,
                channelId, threadId, userMessagePersisted,
                ct.IsCancellationRequested);

            await PersistStreamErrorAsync(channelId, threadId, request, ex,
                userMessagePersisted, ct);
            throw;
        }
        finally
        {
            threadLock?.Dispose();
        }
    }

    public async Task<IReadOnlyList<ChatMessageResponse>> GetHistoryAsync(
        Guid channelId, Guid? threadId = null, int limit = 50, CancellationToken ct = default)
    {
        var hint = threadId is not null
            ? new PersistenceQueryHint("ThreadId", threadId.Value)
            : new PersistenceQueryHint("ChannelId", channelId);

        var hasThread = threadId is not null;
        var messages = await entities.QueryAsync<ChatMessageDB>(
            db,
            m => hasThread ? m.ThreadId == threadId : m.ChannelId == channelId,
            limit,
            hint,
            ct);

        return chatMessages.ToOrderedHistoryResponses(messages);
    }

    // ═══════════════════════════════════════════════════════════════
    // Token cost aggregation
    // ═══════════════════════════════════════════════════════════════

    public async Task<ChannelCostResponse> GetChannelCostAsync(
        Guid channelId, CancellationToken ct = default)
        => await chatCache.GetChannelCostAsync(
            channelId,
            innerCt => LoadChannelCostAsync(channelId, innerCt),
            ct);

    private async Task<ChannelCostResponse> LoadChannelCostAsync(
        Guid channelId, CancellationToken ct)
    {
        var messages = await entities.QueryAsync<ChatMessageDB>(
            db,
            m => m.ChannelId == channelId && m.PromptTokens != null,
            hint: new PersistenceQueryHint("ChannelId", channelId),
            ct: ct);

        return chatCosts.BuildChannelCost(channelId, messages);
    }

    public async Task<ThreadCostResponse?> GetThreadCostAsync(
        Guid channelId, Guid threadId, CancellationToken ct = default)
        => await chatCache.GetThreadCostAsync(
            channelId,
            threadId,
            innerCt => LoadThreadCostAsync(channelId, threadId, innerCt),
            ct);

    private async Task<ThreadCostResponse?> LoadThreadCostAsync(
        Guid channelId, Guid threadId, CancellationToken ct)
    {
        var threadExists = await db.ChatThreads
            .AnyAsync(t => t.Id == threadId && t.ChannelId == channelId, ct);
        if (!threadExists) return null;

        var rows = await entities.QueryAsync<ChatMessageDB>(
            db,
            m => m.ThreadId == threadId && m.PromptTokens != null,
            hint: new PersistenceQueryHint("ThreadId", threadId),
            ct: ct);

        return chatCosts.BuildThreadCost(channelId, threadId, rows);
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

        return await GetAgentCostForKnownAgentAsync(agentId, agent.Name, ct);
    }

    private async Task<AgentCostResponse?> GetAgentCostForKnownAgentAsync(
        Guid agentId, string agentName, CancellationToken ct)
        => await chatCache.GetAgentCostAsync(
            agentId,
            innerCt => LoadAgentCostAsync(agentId, agentName, innerCt),
            ct);

    private async Task<AgentCostResponse?> LoadAgentCostAsync(
        Guid agentId, string agentName, CancellationToken ct)
    {
        var messages = await entities.QueryAsync<ChatMessageDB>(
            db,
            m => m.SenderAgentId == agentId && m.PromptTokens != null,
            hint: new PersistenceQueryHint("SenderAgentId", agentId),
            ct: ct);

        return chatCosts.BuildAgentCost(agentId, agentName, messages);
    }

    private async Task<(ChannelCostResponse ChannelCost, ThreadCostResponse? ThreadCost, AgentCostResponse? AgentCost)> GetResponseCostsAsync(
        Guid channelId, Guid? threadId, Guid agentId, string agentName, CancellationToken ct)
    {
        var channelCost = await GetChannelCostAsync(channelId, ct);
        var threadCost = threadId is { } tid
            ? await GetThreadCostAsync(channelId, tid, ct)
            : null;
        var agentCost = await GetAgentCostForKnownAgentAsync(agentId, agentName, ct);
        return (channelCost, threadCost, agentCost);
    }

    // ═══════════════════════════════════════════════════════════════
    // Agent resolution
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Snapshot of the sender (user) at the moment a chat message is
    /// persisted: their display username and a copy of their currently
    /// assigned permission role. Captured at send time so historical
    /// messages stay accurate even if the user is later renamed or
    /// reassigned to a different role.
    /// </summary>
    private async Task<(string? Username, Guid? RoleId, string? RoleName)> ResolveUserSenderSnapshotAsync(
        Guid? senderUserId, string? externalDisplayName, string? externalUsername, CancellationToken ct)
    {
        if (!senderUserId.HasValue)
            return (externalDisplayName ?? externalUsername, null, null);

        var snapshot = await db.Users
            .Where(u => u.Id == senderUserId.Value)
            .Select(u => new { u.Username, u.RoleId, RoleName = u.Role != null ? u.Role.Name : null })
            .FirstOrDefaultAsync(ct);

        return snapshot is null
            ? (null, null, null)
            : (snapshot.Username, snapshot.RoleId, snapshot.RoleName);
    }

    // ═══════════════════════════════════════════════════════════════
    // Thread history loading
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads messages for a thread, respecting the thread's per-thread
    /// <see cref="ChatThreadDB.MaxMessages"/> and
    /// <see cref="ChatThreadDB.MaxCharacters"/> limits.
    /// Falls back to Core default history limits.
    /// When both limits are set, only messages fitting within both are
    /// returned.
    /// </summary>
    private async Task<(List<ChatCompletionMessage> Messages, int MaxMessages, int MaxCharacters)> LoadThreadHistoryAsync(
        Guid threadId, CancellationToken ct)
    {
        var limits = await LoadThreadHistoryLimitsAsync(threadId, ct);
        var cold = await entities.QueryAsync<ChatMessageDB>(
            db,
            m => m.ThreadId == threadId,
            limit: limits.MaxMessages,
            hint: new PersistenceQueryHint("ThreadId", threadId),
            ct: ct);

        var messages = chatHistory.BuildProviderHistory(
                cold.Select(static m => new ChatHistoryMessage(
                    m.CreatedAt,
                    m.Role,
                    m.Content,
                    m.ProviderMetadataJson)),
                limits)
            .ToList();

        return (messages, limits.MaxMessages, limits.MaxCharacters);
    }

    private async Task<ChatHistoryLimits> LoadThreadHistoryLimitsAsync(
        Guid threadId, CancellationToken ct)
    {
        return await chatCache.GetOrCreateAsync(
            ChatCache.KeyThreadHistoryLimits(threadId),
            async innerCt =>
            {
                var limits = await db.ChatThreads
                    .AsNoTracking()
                    .Where(t => t.Id == threadId)
                    .Select(t => new
                    {
                        t.MaxMessages,
                        t.MaxCharacters
                    })
                    .FirstOrDefaultAsync(innerCt);

                return chatHistory.ResolveLimits(
                    limits?.MaxMessages,
                    limits?.MaxCharacters);
            },
            static _ => 16,
            ct)
            ?? chatHistory.ResolveLimits(null, null);
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
        ChannelDB channel, AgentDB agent, string clientType,
        CancellationToken ct,
        TaskChatContext? taskContext = null,
        string? externalUsername = null, string? externalDisplayName = null,
        CompletionParameters? completionParameters = null,
        string providerKey = "")
    {
        if (chatHeaders.IsHeaderDisabled(channel))
            return null;

        // Custom headers are explicit operator configuration and remain
        // available when the generated default header is disabled globally.
        var customTemplate = chatHeaders.ResolveCustomTemplate(channel, agent);
        if (customTemplate is not null)
        {
            var userId2 = jobService.GetSessionUserId();
            return await headerTagProcessor.ExpandAsync(
                customTemplate, channel, agent, clientType, userId2, ct,
                completionParameters, providerKey);
        }

        if (!chatHeaders.ShouldBuildDefaultHeader(_disableDefaultChatHeaders))
            return null;

        // ── Task-sourced message: lightweight header, no user lookup ──
        if (taskContext is not null)
        {
            var store = TaskSharedData.Get(taskContext.InstanceId);
            string? lightText = null;
            IReadOnlyList<ChatTaskBigDataReference> bigEntries = [];
            if (store is not null)
            {
                lightText = store.LightData;
                bigEntries = store.ListBig()
                    .Select(static e => new ChatTaskBigDataReference(e.Id, e.Title))
                    .ToArray();
            }

            var suffix = await LoadAgentSuffixAsync(agent.Id, channel.Id, ct,
                completionParameters, providerKey);
            return chatHeaders.BuildTaskHeader(
                new ChatTaskHeaderFacts(
                    taskContext.TaskName,
                    lightText,
                    bigEntries),
                suffix,
                DateTimeOffset.UtcNow);
        }

        var userId = jobService.GetSessionUserId();

        // ── External user (bot-forwarded message): no DB session ─────
        if (userId is null && externalUsername is not null)
        {
            var suffix = await LoadAgentSuffixAsync(agent.Id, channel.Id, ct,
                completionParameters, providerKey);
            return chatHeaders.BuildExternalUserHeader(
                new ChatExternalUserHeaderFacts(
                    externalUsername,
                    externalDisplayName,
                    clientType),
                suffix,
                DateTimeOffset.UtcNow);
        }

        if (userId is null)
            return null;

        var userState = await chatCache.GetOrCreateAsync(
            ChatCache.KeyHeaderUser(userId.Value),
            innerCt => LoadUserHeaderStateAsync(userId.Value, innerCt),
            EstimateUserHeaderState,
            ct);

        if (userState is null)
            return null;

        var userSuffix = await LoadAgentSuffixAsync(agent.Id, channel.Id, ct,
            completionParameters, providerKey);
        return chatHeaders.BuildAuthenticatedUserHeader(
            new ChatAuthenticatedUserHeaderFacts(
                userState.Username,
                clientType,
                userState.RoleName,
                userState.Grants,
                userState.Bio),
            userSuffix,
            DateTimeOffset.UtcNow);
    }

    private async Task<UserHeaderState?> LoadUserHeaderStateAsync(
        Guid userId, CancellationToken ct)
    {
        var user = await db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .ThenInclude(r => r!.PermissionSet)
            .AsSplitQuery()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return null;

        var grants = new List<string>();
        if (user.Role?.PermissionSetId is { } psId)
        {
            var ps = await db.PermissionSets
                .AsNoTracking()
                .Include(p => p.GlobalFlags)
                .Include(p => p.ResourceAccesses)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == psId, ct);

            if (ps is not null)
            {
                grants = [.. await headerGrantFormatter.FormatGrantNamesWithResourcesAsync(
                    ps,
                    serviceProvider,
                    ct)];
            }
        }

        return new UserHeaderState(
            user.Username,
            user.Role?.Name,
            grants,
            user.Bio);
    }

    private static long EstimateUserHeaderState(UserHeaderState state)
        => 128
           + ChatCache.EstimateString(state.Username)
           + ChatCache.EstimateString(state.RoleName)
           + ChatCache.EstimateString(state.Bio)
           + ChatCache.EstimateStringCollection(state.Grants);

    private async Task<string?> LoadAgentSuffixAsync(
        Guid agentId, Guid channelId,
        CancellationToken ct,
        CompletionParameters? completionParameters = null,
        string providerKey = "")
    {
        return await chatCache.GetOrCreateAsync(
            ChatCache.KeyHeaderAgentSuffix(
                agentId,
                channelId,
                providerKey,
                completionParameters?.ReasoningEffort),
            async innerCt => await BuildAgentSuffixTextAsync(
                agentId, channelId, innerCt, completionParameters, providerKey),
            ChatCache.EstimateString,
            ct);
    }

    /// <summary>
    /// Appends the shared agent-role, policy, and closing bracket to a
    /// header being constructed.
    /// Shared across all header paths (authenticated user, external user, task).
    /// </summary>
    private async Task<string> BuildAgentSuffixTextAsync(
        Guid agentId, Guid channelId,
        CancellationToken ct,
        CompletionParameters? completionParameters = null,
        string providerKey = "")
    {
        var agentWithRole = await db.Agents
            .AsNoTracking()
            .Include(a => a.Role)
            .ThenInclude(r => r!.PermissionSet)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        string? roleName = null;
        IReadOnlyList<string> grants = [];
        if (agentWithRole?.Role is { } agentRole)
        {
            PermissionSetDB? agentPs = null;
            if (agentRole.PermissionSetId is { } agentPsId)
            {
                agentPs = await db.PermissionSets
                        .AsNoTracking()
                        .Include(p => p.GlobalFlags)
                        .Include(p => p.ResourceAccesses)
                        .AsSplitQuery()
                        .FirstOrDefaultAsync(p => p.Id == agentPsId, ct);
            }

            roleName = agentRole.Name;
            if (agentPs is not null)
            {
                grants = await headerGrantFormatter.FormatGrantNamesWithResourcesAsync(
                    agentPs,
                    serviceProvider,
                    ct);
            }
        }

        return chatHeaders.BuildAgentSuffix(
            new ChatAgentHeaderSuffixFacts(roleName, grants),
            completionParameters,
            providerKey);
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
        var timingRequestId = Guid.NewGuid().ToString("N")[..8];
        var totalTiming = Stopwatch.StartNew();
        var logTiming = logger.IsEnabled(LogLevel.Debug);
        var streamCompleted = false;
        var userMessagePersisted = false;
        var assistantMessagePersisted = false;
        var streamedContent = new StringBuilder();

        if (logTiming)
        {
            logger.LogDebug(
                "Streaming chat request {RequestId} started. ChannelId={ChannelId} ThreadId={ThreadId} RequestedAgentId={RequestedAgentId} ClientType={ClientType} MessageChars={MessageChars} CancellationRequested={CancellationRequested}",
                timingRequestId, channelId, threadId, request.AgentId,
                PathGuard.SanitizeForLog(request.ClientType),
                request.Message.Length, ct.IsCancellationRequested);
        }

        var channel = await db.Channels
            .AsNoTracking()
            .Include(c => c.Agent!).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent!).ThenInclude(a => a.ToolAwarenessSet)
            .Include(c => c.Agent!).ThenInclude(a => a.Role)
            .Include(c => c.ToolAwarenessSet)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.ToolAwarenessSet)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Role)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == channelId, ct)
            ?? throw new ArgumentException($"Channel {channelId} not found.");

        var agent = conversation.ResolveRequestedAgent(channel, request.AgentId);
        var plan = chatPlanner.BuildStreamingPlan(
            channel,
            agent,
            threadId,
            _disableDefaultSystemPrompt,
            _disableCustomProviderParameters);
        var model = agent.Model!;
        var provider = model.Provider!;

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
        var historyTiming = Stopwatch.StartNew();
        List<ChatCompletionMessage> history;
        int? maxHistoryMessages = null;
        int? maxHistoryCharacters = null;
        if (threadId is not null)
        {
            var historyLoad = await LoadThreadHistoryAsync(threadId.Value, ct);
            history = historyLoad.Messages;
            maxHistoryMessages = historyLoad.MaxMessages;
            maxHistoryCharacters = historyLoad.MaxCharacters;
        }
        else
        {
            history = [];
        }
        historyTiming.Stop();

        if (logTiming)
        {
            logger.LogDebug(
                "Streaming chat request {RequestId} loaded history in {HistoryLoadMs}ms. ThreadId={ThreadId} HistoryMessages={HistoryMessages} HistoryChars={HistoryChars} MaxHistoryMessages={MaxHistoryMessages} MaxHistoryCharacters={MaxHistoryCharacters} ElapsedMs={ElapsedMs}",
                timingRequestId, historyTiming.ElapsedMilliseconds, threadId,
                history.Count, history.Sum(m => m.Content.Length),
                maxHistoryMessages, maxHistoryCharacters,
                totalTiming.ElapsedMilliseconds);
        }

        history.Add(new ChatCompletionMessage(ChatRoles.User, request.Message));

        var apiKey = plan.RequiresApiKey
            ? ApiKeyEncryptor.DecryptOrPassthrough(provider.EncryptedApiKey!, encryptionOptions.Key)
            : "local";
        var client = plan.Client;
        var systemPrompt = plan.SystemPrompt;
        var completionParams = plan.CompletionParameters;

        // Build chat header for the user message (if enabled)
        var chatHeader = await BuildChatHeaderAsync(channel, agent, request.ClientType, ct,
            taskContext: request.TaskContext, externalUsername: request.ExternalUsername, externalDisplayName: request.ExternalDisplayName,
            completionParameters: completionParams, providerKey: provider.ProviderKey);
        if (chatHeader is not null)
            history[^1] = new ChatCompletionMessage(ChatRoles.User, chatHeader + request.Message);

        using var httpClient = httpClientFactory.CreateClient();

        var supportsVision = plan.SupportsVision;
        var maxTokens = plan.MaxCompletionTokens;
        var providerParams = plan.ProviderParameters;
        var toolAwareness = plan.ToolAwareness;
        var effectiveTools = plan.EnableTools
            ? await GetEffectiveToolsAsync(request.TaskContext, toolAwareness, agent.Id, ct)
            : [];

        if (logTiming)
        {
            logger.LogDebug(
                "Streaming chat request {RequestId} prepared provider stream. AgentId={AgentId} AgentName={AgentName} ModelId={ModelId} ModelName={ModelName} ProviderKey={ProviderKey} ProviderName={ProviderName} SystemPromptChars={SystemPromptChars} SupportsVision={SupportsVision} EffectiveTools={EffectiveTools} MaxCompletionTokens={MaxCompletionTokens} ProviderParametersPresent={ProviderParametersPresent} CompletionParametersPresent={CompletionParametersPresent} ElapsedMs={ElapsedMs}",
                timingRequestId, agent.Id, PathGuard.SanitizeForLog(agent.Name),
                model.Id, PathGuard.SanitizeForLog(model.Name),
                PathGuard.SanitizeForLog(provider.ProviderKey),
                PathGuard.SanitizeForLog(provider.Name),
                systemPrompt.Length, supportsVision, effectiveTools.Count,
                maxTokens, providerParams is not null,
                completionParams is not null, totalTiming.ElapsedMilliseconds);
        }

        // Persist user message immediately so it gets an accurate
        // CreatedAt and survives crashes during LLM inference.
        var senderUserId = jobService.GetSessionUserId();
        var senderUserSnapshot = await ResolveUserSenderSnapshotAsync(
            senderUserId, request.ExternalDisplayName, request.ExternalUsername, ct);

        var userMessage = chatMessages.CreateUserMessage(
            channelId,
            threadId,
            request,
            senderUserId,
            senderUserSnapshot.Username,
            senderUserSnapshot.RoleId,
            senderUserSnapshot.RoleName);

        db.ChatMessages.Add(userMessage);
        await db.SaveChangesAsync(CancellationToken.None);
        userMessagePersisted = true;

        // Convert history to tool-aware messages
        var messages = new List<ToolAwareMessage>(history.Count);
        foreach (var msg in history)
        {
            messages.Add(new ToolAwareMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ProviderMetadataJson = msg.ProviderMetadataJson
            });
        }

        var jobResults = new List<AgentJobResponse>();
        var fullContent = new StringBuilder();
        var rounds = 0;
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var roundJobIds = new List<Guid>();
        string? finalProviderMetadataJson = null;
        var providerRound = 0;
        var inlinePermissionCache = new Dictionary<ChatInlineToolPermissionCacheKey, AgentActionResult>();

        while (true)
        {
            // Stream the current round
            providerRound++;
            var providerRoundTiming = Stopwatch.StartNew();
            ChatCompletionResult? roundResult = null;
            var roundDeltaContent = new StringBuilder();

            await foreach (var chunk in client.StreamChatCompletionWithToolsAsync(
                httpClient, apiKey, model.Name, systemPrompt, messages, effectiveTools, maxTokens, providerParams, completionParams, ct))
            {
                if (chunk.Delta is not null)
                {
                    roundDeltaContent.Append(chunk.Delta);
                    streamedContent.Append(chunk.Delta);
                    yield return ChatStreamEvent.TextDelta(chunk.Delta);
                }

                if (chunk.IsFinished)
                    roundResult = chunk.Finished;
            }

            if (roundResult is null)
            {
                fullContent.Append(roundDeltaContent);
                providerRoundTiming.Stop();
                if (logTiming)
                {
                    logger.LogDebug(
                        "Streaming chat request {RequestId} provider round {Round} ended without a finished result after {ProviderRoundMs}ms. ElapsedMs={ElapsedMs}",
                        timingRequestId, providerRound,
                        providerRoundTiming.ElapsedMilliseconds,
                        totalTiming.ElapsedMilliseconds);
                }
                break;
            }
            providerRoundTiming.Stop();

            if (roundResult.Usage is { } roundUsage)
            {
                totalPromptTokens += roundUsage.PromptTokens;
                totalCompletionTokens += roundUsage.CompletionTokens;
            }

            if (logTiming)
            {
                logger.LogDebug(
                    "Streaming chat request {RequestId} provider round {Round} completed in {ProviderRoundMs}ms. ToolCalls={ToolCalls} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} ContentChars={ContentChars} ElapsedMs={ElapsedMs}",
                    timingRequestId, providerRound,
                    providerRoundTiming.ElapsedMilliseconds,
                    roundResult.ToolCalls.Count,
                    roundResult.Usage?.PromptTokens ?? 0,
                    roundResult.Usage?.CompletionTokens ?? 0,
                    roundResult.Content?.Length ?? 0,
                    totalTiming.ElapsedMilliseconds);
            }

            var roundContent = roundResult.Content;
            if (string.IsNullOrEmpty(roundContent) && roundDeltaContent.Length > 0)
                roundContent = roundDeltaContent.ToString();
            fullContent.Append(roundContent ?? "");

            if (!roundResult.HasToolCalls || ++rounds > MaxToolCallRounds)
            {
                finalProviderMetadataJson = roundResult.ProviderMetadataJson;
                break;
            }

            // Record assistant turn with tool calls
            messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                roundResult.ToolCalls,
                roundResult.Content,
                roundResult.ProviderMetadataJson));

            // Reset content for next round (tool results will produce new text)
            fullContent.Clear();
            roundJobIds.Clear();

            foreach (var tc in roundResult.ToolCalls)
            {
                // ── Task-specific tool interception ──────────────
                var (handled, taskResult) = await TryHandleTaskToolAsync(
                    tc, request.TaskContext, ct);
                if (handled)
                {
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, taskResult ?? ""));
                    var taskNotation = FormatTaskToolNotation(tc.Name);
                    fullContent.Append(taskNotation);
                    streamedContent.Append(taskNotation);
                    yield return ChatStreamEvent.TextDelta(taskNotation);
                    continue;
                }

                // ── Inline module tool interception ──────────────
                if (moduleRegistry.IsInlineTool(tc.Name))
                {
                    var inlineResult = await HandleInlineModuleToolAsync(
                        tc, agent.Id, channelId, threadId, inlinePermissionCache, ct);
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, inlineResult));
                    var inlineNotation = FormatInlineToolNotation(tc.Name);
                    fullContent.Append(inlineNotation);
                    streamedContent.Append(inlineNotation);
                    yield return ChatStreamEvent.TextDelta(inlineNotation);
                    continue;
                }

                var nativeTool = await ExecuteNativeJobToolAsync(
                    tc,
                    agent.Id,
                    channelId,
                    supportsVision,
                    emitStreamEvents: true,
                    approvalCallback,
                    ct);

                messages.Add(nativeTool.ToolResultMessage);
                if (!nativeTool.Parsed)
                    continue;

                if (nativeTool.SubmittedJobId is { } submittedJobId)
                    roundJobIds.Add(submittedJobId);

                // Core owns native job approval and stream-event selection.
                foreach (var streamEvent in nativeTool.StreamEvents)
                    yield return streamEvent;

                if (nativeTool.JobResponse is not null)
                    jobResults.Add(nativeTool.JobResponse);

                fullContent.Append(nativeTool.ToolNotation);
                streamedContent.Append(nativeTool.ToolNotation);
            }

            // Distribute this round's token usage across jobs submitted in the round
            if (roundJobIds.Count > 0 && roundResult.Usage is { } ru)
            {
                await jobService.RecordTokensAsync(roundJobIds, ru.PromptTokens, ru.CompletionTokens, ct);
                chatToolResults.ApplyRoundTokenUsageToJobResponses(
                    jobResults,
                    roundJobIds,
                    ru.PromptTokens,
                    ru.CompletionTokens);
            }
        }

        // Persist assistant message after LLM completes
        var assistantContent = fullContent.ToString();

        var assistantMessage = chatMessages.CreateAssistantMessage(
            channelId,
            threadId,
            request,
            agent,
            assistantContent,
            totalPromptTokens,
            totalCompletionTokens,
            finalProviderMetadataJson);

        db.ChatMessages.Add(assistantMessage);
        var assistantSaveTiming = Stopwatch.StartNew();
        await db.SaveChangesAsync(CancellationToken.None);
        assistantMessagePersisted = true;
        assistantSaveTiming.Stop();

        await jobService.RecordTokensForCurrentExecutionAsync(
            totalPromptTokens, totalCompletionTokens, ct);
        chatCache.RecordAssistantTokens(
            channelId,
            threadId,
            agent.Id,
            agent.Name,
            totalPromptTokens,
            totalCompletionTokens);

        if (logTiming)
        {
            logger.LogDebug(
                "Streaming chat request {RequestId} saved assistant message in {AssistantSaveMs}ms. AssistantMessageId={AssistantMessageId} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} AssistantContentChars={AssistantContentChars} JobResults={JobResultCount} ElapsedMs={ElapsedMs}",
                timingRequestId, assistantSaveTiming.ElapsedMilliseconds,
                assistantMessage.Id, totalPromptTokens, totalCompletionTokens,
                assistantContent.Length, jobResults.Count,
                totalTiming.ElapsedMilliseconds);
        }

        if (threadId is not null)
            threadActivity.Publish(threadId.Value,
                new ThreadActivityEvent(ThreadActivityEventType.NewMessages, request.ClientType));

        var costTiming = Stopwatch.StartNew();
        var (channelCost, threadCost, agentCost) =
            await GetResponseCostsAsync(channelId, threadId, agent.Id, agent.Name, ct);
        costTiming.Stop();

        streamCompleted = true;
        if (logTiming)
        {
            logger.LogDebug(
                "Streaming chat request {RequestId} completed in {ElapsedMs}ms. ProviderRounds={ProviderRounds} CostLoadMs={CostLoadMs} ChannelTokens={ChannelTokens} ThreadTokens={ThreadTokens} AgentTokens={AgentTokens}",
                timingRequestId, totalTiming.ElapsedMilliseconds,
                providerRound, costTiming.ElapsedMilliseconds,
                channelCost.TotalTokens, threadCost?.TotalTokens,
                agentCost?.TotalTokens);
        }

        yield return ChatStreamEvent.Complete(new ChatResponse(
            chatMessages.ToResponse(userMessage),
            chatMessages.ToResponse(assistantMessage),
            jobResults.Count > 0 ? jobResults : null,
            channelCost,
            threadCost,
            agentCost));

        } // try
        finally
        {
            if (!streamCompleted && logTiming)
            {
                logger.LogDebug(
                    "Streaming chat request {RequestId} ended before completion after {ElapsedMs}ms. ChannelId={ChannelId} ThreadId={ThreadId} UserMessagePersisted={UserMessagePersisted} AssistantMessagePersisted={AssistantMessagePersisted} PartialChars={PartialChars} CancellationRequested={CancellationRequested}",
                    timingRequestId, totalTiming.ElapsedMilliseconds,
                    channelId, threadId, userMessagePersisted,
                    assistantMessagePersisted, streamedContent.Length,
                    ct.IsCancellationRequested);
            }

            if (!streamCompleted && userMessagePersisted && !assistantMessagePersisted && streamedContent.Length > 0)
            {
                await PersistPartialAssistantMessageAsync(
                    channelId,
                    threadId,
                    request,
                    agent,
                    streamedContent.ToString(),
                    totalPromptTokens: null,
                    totalCompletionTokens: null,
                    providerMetadataJson: null);
            }

            threadLock?.Dispose();
        }
    }

    /// <summary>
    /// Persists the assistant text emitted before a stream was interrupted.
    /// </summary>
    private async Task PersistPartialAssistantMessageAsync(
        Guid channelId,
        Guid? threadId,
        ChatRequest request,
        AgentDB agent,
        string content,
        int? totalPromptTokens,
        int? totalCompletionTokens,
        string? providerMetadataJson)
    {
        try
        {
            var assistantMessage = chatMessages.CreateAssistantMessage(
                channelId,
                threadId,
                request,
                agent,
                content,
                totalPromptTokens,
                totalCompletionTokens,
                providerMetadataJson);

            db.ChatMessages.Add(assistantMessage);
            await db.SaveChangesAsync(CancellationToken.None);

            if (threadId is not null)
                threadActivity.Publish(threadId.Value,
                    new ThreadActivityEvent(ThreadActivityEventType.NewMessages, request.ClientType));

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Persisted partial assistant message after interrupted stream. ChannelId={ChannelId} ThreadId={ThreadId} AssistantMessageId={AssistantMessageId} ContentChars={ContentChars}",
                    channelId, threadId, assistantMessage.Id, content.Length);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to persist partial assistant message after interrupted stream. ChannelId={ChannelId} ThreadId={ThreadId}",
                channelId, threadId);
        }
    }

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
                var senderUserSnapshot = await ResolveUserSenderSnapshotAsync(
                    senderUserId, request.ExternalDisplayName, request.ExternalUsername, ct);

                db.ChatMessages.Add(chatMessages.CreateUserMessage(
                    channelId,
                    threadId,
                    request,
                    senderUserId,
                    senderUserSnapshot.Username,
                    senderUserSnapshot.RoleId,
                    senderUserSnapshot.RoleName));
            }

            db.ChatMessages.Add(chatMessages.CreateSystemErrorMessage(
                channelId,
                threadId,
                request,
                ex.Message));

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
            // Internal dedup uses Origin where available; legacy rows
            // (Origin == null) are matched on the provider Role string.
            var userAlreadySaved = await db.ChatMessages.AnyAsync(
                m => m.ChannelId == channelId
                    && m.ThreadId == threadId
                    && (m.Origin == MessageOrigin.User
                        || (m.Origin == null && m.Role == ChatRoles.User))
                    && m.Content == request.Message,
                ct);

            if (!userAlreadySaved)
            {
                var senderUserId = jobService.GetSessionUserId();
                var senderUserSnapshot = await ResolveUserSenderSnapshotAsync(
                    senderUserId, request.ExternalDisplayName, request.ExternalUsername, ct);

                db.ChatMessages.Add(chatMessages.CreateUserMessage(
                    channelId,
                    threadId,
                    request,
                    senderUserId,
                    senderUserSnapshot.Username,
                    senderUserSnapshot.RoleId,
                    senderUserSnapshot.RoleName));
            }

            db.ChatMessages.Add(chatMessages.CreateSystemErrorMessage(
                channelId,
                threadId,
                request,
                errorMessage));

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
    /// Returns the effective tool list for a chat call.  When a task context is
    /// present, task-scoped tools (shared data, output, custom hooks) are appended.
    /// The tool-awareness filter is applied last so it can suppress any tool by name.
    /// </summary>
    private async Task<IReadOnlyList<ChatToolDefinition>> GetEffectiveToolsAsync(
        TaskChatContext? taskContext,
        Dictionary<string, bool>? toolAwareness = null,
        Guid? agentId = null,
        CancellationToken ct = default)
    {
        if (taskContext is null && agentId.HasValue)
        {
            return await chatCache.GetOrCreateAsync(
                ChatCache.KeyEffectiveTools(
                    agentId.Value,
                    chatToolSelection.BuildAwarenessFingerprint(toolAwareness)),
                async _ => (IReadOnlyList<ChatToolDefinition>?)
                    await BuildEffectiveToolsAsync(null, toolAwareness),
                chatToolSelection.EstimateToolDefinitions,
                ct)
                ?? [];
        }

        return await BuildEffectiveToolsAsync(taskContext, toolAwareness);
    }

    private Task<IReadOnlyList<ChatToolDefinition>> BuildEffectiveToolsAsync(
        TaskChatContext? taskContext,
        Dictionary<string, bool>? toolAwareness)
    {
        var baseTools = new List<ChatToolDefinition>(moduleRegistry.GetAllToolDefinitions());

        // In-flight task-context tools (shared data, output, custom hooks)
        if (taskContext is not null)
        {
            var store = TaskSharedData.Get(taskContext.InstanceId);
            if (store is not null)
                baseTools.AddRange(store.GetToolDefinitions());
        }

        return Task.FromResult(
            chatToolSelection.ApplyAwareness(baseTools, toolAwareness));
    }

    /// <summary>
    /// Try to handle a native tool call as a task-specific tool.
    /// <para>
    /// Handles in-flight task-context tools (shared data, output, custom hooks)
    /// when <paramref name="taskContext"/> is present.
    /// </para>
    /// Returns <c>true</c> and sets <paramref name="result"/> if handled.
    /// </summary>
    private async Task<(bool Handled, string? Result)> TryHandleTaskToolAsync(
        ChatToolCall toolCall,
        TaskChatContext? taskContext,
        CancellationToken ct)
    {
        if (taskContext is not null)
        {
            var store = TaskSharedData.Get(taskContext.InstanceId);
            if (store is not null)
            {
                try
                {
                    JsonElement? args = null;
                    if (!string.IsNullOrEmpty(toolCall.ArgumentsJson))
                        args = JsonDocument.Parse(toolCall.ArgumentsJson).RootElement;

                    var handled = await store.TryInvokeToolAsync(toolCall.Name, args, ct);
                    if (handled.Handled)
                        return handled;
                }
                catch (Exception ex)
                {
                    return (true, $"Error handling task tool '{toolCall.Name}': {ex.Message}");
                }
            }
        }

        return (false, null);
    }

    /// <summary>
    /// Dispatches an inline module tool call.  Resolves the owning module
    /// from <see cref="ModuleRegistry"/>, creates a restricted
    /// <see cref="ModuleServiceScope"/>, and calls
    /// <see cref="ISharpClawCoreModule.ExecuteInlineToolAsync"/>.
    /// </summary>
    private async Task<string> HandleInlineModuleToolAsync(
        ChatToolCall toolCall,
        Guid agentId,
        Guid channelId,
        Guid? threadId,
        Dictionary<ChatInlineToolPermissionCacheKey, AgentActionResult> permissionCache,
        CancellationToken ct)
    {
        var result = await chatInlineToolExecutor.ExecuteAsync(
            new ChatInlineToolExecutionRequest(
                toolCall,
                agentId,
                channelId,
                threadId,
                moduleRegistry,
                permissionCache,
                CheckInlineToolPermissionAsync,
                serviceProvider,
                ModuleHostServiceAccess.BlockedServiceTypes),
            ct);

        if (result.ModuleInvoked
            && result.PrefixedToolName is { } prefixedToolName
            && logger.IsEnabled(LogLevel.Debug))
        {
            var sanitizedToolName = PathGuard.SanitizeForLog(prefixedToolName);
            if (result.Succeeded)
            {
                logger.LogDebug(
                    "Inline module tool {ToolName} completed in {ElapsedMs}ms. AgentId={AgentId} ChannelId={ChannelId} ThreadId={ThreadId}",
                    sanitizedToolName,
                    result.Elapsed.TotalMilliseconds,
                    agentId,
                    channelId,
                    threadId);
            }
            else
            {
                logger.LogDebug(
                    result.Exception,
                    "Inline module tool {ToolName} failed in {ElapsedMs}ms. AgentId={AgentId} ChannelId={ChannelId} ThreadId={ThreadId}",
                    sanitizedToolName,
                    result.Elapsed.TotalMilliseconds,
                    agentId,
                    channelId,
                    threadId);
            }
        }

        return result.ToolResult;
    }

    private Task<AgentActionResult> CheckInlineToolPermissionAsync(
        ChatInlineToolPermissionCheck check,
        CancellationToken ct) =>
        jobService.CheckPermissionAsync(
            check.AgentId,
            resourceId: null,
            new ActionCaller(AgentId: check.AgentId),
            ct,
            actionKey: check.ActionKey);

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
        IReadOnlySet<string> modelCapabilityTags,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
        CancellationToken ct,
        TaskChatContext? taskContext = null,
        Dictionary<string, bool>? toolAwareness = null,
        Guid? threadId = null,
        string? timingRequestId = null,
        Stopwatch? totalTiming = null)
    {
        var messages = new List<ToolAwareMessage>(dbHistory.Count);
        foreach (var msg in dbHistory)
        {
            messages.Add(new ToolAwareMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ProviderMetadataJson = msg.ProviderMetadataJson
            });
        }

        var supportsVision = modelCapabilityTags.Contains(WellKnownCapabilityKeys.Vision);
        var jobResults = new List<AgentJobResponse>();
        var toolNotation = new StringBuilder();
        var rounds = 0;
        var effectiveTools = await GetEffectiveToolsAsync(taskContext, toolAwareness, agentId, ct);
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var roundJobIds = new List<Guid>();
        var logTiming = timingRequestId is not null && logger.IsEnabled(LogLevel.Debug);
        var providerRound = 0;
        var inlinePermissionCache = new Dictionary<ChatInlineToolPermissionCacheKey, AgentActionResult>();

        if (logTiming)
        {
            logger.LogDebug(
                "Chat request {RequestId} resolved native tools. AgentId={AgentId} ChannelId={ChannelId} ThreadId={ThreadId} EffectiveTools={EffectiveTools} HistoryMessages={HistoryMessages} SupportsVision={SupportsVision} ElapsedMs={ElapsedMs}",
                timingRequestId, agentId, channelId, threadId,
                effectiveTools.Count, dbHistory.Count, supportsVision,
                totalTiming?.ElapsedMilliseconds);
        }

        while (true)
        {
            providerRound++;
            var providerRoundTiming = Stopwatch.StartNew();
            ChatCompletionResult result;
            try
            {
                result = await client.ChatCompletionWithToolsAsync(
                    httpClient, apiKey, modelName, systemPrompt, messages, effectiveTools, maxCompletionTokens, providerParameters, completionParameters, ct);
            }
            catch (LocalInferenceEnvelopeException ex)
            {
                providerRoundTiming.Stop();
                logger.LogWarning(
                    ex,
                    "Local-inference tool loop aborted for chat request {RequestId} after {ProviderRoundMs}ms: malformed envelope from model. Preview={Preview}",
                    timingRequestId, providerRoundTiming.ElapsedMilliseconds,
                    ex.PayloadPreview);

                return BuildEnvelopeFailureResult(
                    toolNotation,
                    jobResults,
                    totalPromptTokens,
                    totalCompletionTokens,
                    ex);
            }
            providerRoundTiming.Stop();

            if (result.Usage is { } usage)
            {
                totalPromptTokens += usage.PromptTokens;
                totalCompletionTokens += usage.CompletionTokens;
            }

            if (logTiming)
            {
                logger.LogDebug(
                    "Chat request {RequestId} native provider round {Round} completed in {ProviderRoundMs}ms. ToolCalls={ToolCalls} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} ContentChars={ContentChars} ElapsedMs={ElapsedMs}",
                    timingRequestId, providerRound,
                    providerRoundTiming.ElapsedMilliseconds,
                    result.ToolCalls.Count,
                    result.Usage?.PromptTokens ?? 0,
                    result.Usage?.CompletionTokens ?? 0,
                    result.Content?.Length ?? 0,
                    totalTiming?.ElapsedMilliseconds);
            }

            if (!result.HasToolCalls || ++rounds > MaxToolCallRounds)
            {
                var finalContent = chatToolResults.BuildFinalAssistantContent(
                    toolNotation.ToString(),
                    result.Content);
                return new ToolLoopResult(
                    finalContent,
                    jobResults,
                    totalPromptTokens,
                    totalCompletionTokens,
                    result.ProviderMetadataJson);
            }

            // Record assistant turn with tool calls
            messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                result.ToolCalls,
                result.Content,
                result.ProviderMetadataJson));

            var anyUnresolvableApproval = false;
            roundJobIds.Clear();

            foreach (var tc in result.ToolCalls)
            {
                // ── Task-specific tool interception ──────────────
                var (handled, taskResult) = await TryHandleTaskToolAsync(
                    tc, taskContext, ct);
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
                        tc, agentId, channelId, threadId, inlinePermissionCache, ct);
                    messages.Add(ToolAwareMessage.ToolResult(tc.Id, inlineResult));
                    toolNotation.Append(FormatInlineToolNotation(tc.Name));
                    continue;
                }

                var nativeTool = await ExecuteNativeJobToolAsync(
                    tc,
                    agentId,
                    channelId,
                    supportsVision,
                    emitStreamEvents: false,
                    approvalCallback,
                    ct);

                messages.Add(nativeTool.ToolResultMessage);
                if (!nativeTool.Parsed)
                    continue;

                if (nativeTool.SubmittedJobId is { } submittedJobId)
                    roundJobIds.Add(submittedJobId);

                // Core owns native job approval and unresolved-approval detection.
                if (nativeTool.JobResponse is not null)
                    jobResults.Add(nativeTool.JobResponse);

                toolNotation.Append(nativeTool.ToolNotation);

                if (nativeTool.AwaitingUnresolvableApproval)
                    anyUnresolvableApproval = true;
            }

            // Distribute this round's token usage across jobs submitted in the round
            if (roundJobIds.Count > 0 && result.Usage is { } ru)
            {
                await jobService.RecordTokensAsync(roundJobIds, ru.PromptTokens, ru.CompletionTokens, ct);
                chatToolResults.ApplyRoundTokenUsageToJobResponses(
                    jobResults,
                    roundJobIds,
                    ru.PromptTokens,
                    ru.CompletionTokens);
            }

            if (anyUnresolvableApproval)
            {
                ChatCompletionResult finalResult;
                var finalApprovalTiming = Stopwatch.StartNew();
                try
                {
                    finalResult = await client.ChatCompletionWithToolsAsync(
                        httpClient, apiKey, modelName, systemPrompt, messages, effectiveTools, maxCompletionTokens, providerParameters, completionParameters, ct);
                }
                catch (LocalInferenceEnvelopeException ex)
                {
                    finalApprovalTiming.Stop();
                    logger.LogWarning(
                        ex,
                        "Local-inference final approval round aborted for chat request {RequestId} after {ProviderRoundMs}ms: malformed envelope from model. Preview={Preview}",
                        timingRequestId, finalApprovalTiming.ElapsedMilliseconds,
                        ex.PayloadPreview);

                    return BuildEnvelopeFailureResult(
                        toolNotation,
                        jobResults,
                        totalPromptTokens,
                        totalCompletionTokens,
                        ex);
                }
                finalApprovalTiming.Stop();
                if (finalResult.Usage is { } finalUsage)
                {
                    totalPromptTokens += finalUsage.PromptTokens;
                    totalCompletionTokens += finalUsage.CompletionTokens;
                }
                if (logTiming)
                {
                    logger.LogDebug(
                        "Chat request {RequestId} final approval provider round completed in {ProviderRoundMs}ms. PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} ContentChars={ContentChars} ElapsedMs={ElapsedMs}",
                        timingRequestId, finalApprovalTiming.ElapsedMilliseconds,
                        finalResult.Usage?.PromptTokens ?? 0,
                        finalResult.Usage?.CompletionTokens ?? 0,
                        finalResult.Content?.Length ?? 0,
                        totalTiming?.ElapsedMilliseconds);
                }
                var finalContent = chatToolResults.BuildFinalAssistantContent(
                    toolNotation.ToString(),
                    finalResult.Content);
                return new ToolLoopResult(
                    finalContent,
                    jobResults,
                    totalPromptTokens,
                    totalCompletionTokens,
                    finalResult.ProviderMetadataJson);
            }
        }
    }

    /// <summary>
    /// Converts a malformed local-inference tool-loop envelope into a normal
    /// assistant-visible error result instead of letting the exception bubble
    /// out as a 500. The earlier tool results (and their authoritative
    /// notation lines) are preserved so the user can still see what happened
    /// before the local model lost the grammar and emitted invalid JSON.
    /// <para>
    /// This preserves the L-017 contract: the local-inference API client
    /// still throws a typed exception on malformed envelopes, but the caller
    /// turns it into a user-facing error response instead of a transport-level
    /// failure.
    /// </para>
    /// </summary>
    private ToolLoopResult BuildEnvelopeFailureResult(
        StringBuilder toolNotation,
        List<AgentJobResponse> jobResults,
        int totalPromptTokens,
        int totalCompletionTokens,
        LocalInferenceEnvelopeException ex)
    {
        return new ToolLoopResult(
            chatToolResults.BuildMalformedEnvelopeAssistantContent(
                toolNotation.ToString(),
                ex.PayloadPreview),
            jobResults,
            totalPromptTokens,
            totalCompletionTokens);
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
            result.Usage?.CompletionTokens ?? 0,
            result.ProviderMetadataJson);
    }

    /// <summary>
    /// Parses a native <see cref="ChatToolCall"/> into the internal
    /// <see cref="ParsedChatToolCall"/> representation. Returns <see langword="null"/>
    /// if the tool name is unrecognized or the arguments are malformed.
    /// All tool definitions are resolved via <see cref="ModuleRegistry"/>.
    /// </summary>
    private Task<ChatNativeJobToolExecutionResult> ExecuteNativeJobToolAsync(
        ChatToolCall toolCall,
        Guid agentId,
        Guid channelId,
        bool supportsVision,
        bool emitStreamEvents,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
        CancellationToken ct)
        => chatNativeJobToolExecutor.ExecuteAsync(
            new ChatNativeJobToolExecutionRequest(
                BuildNativeToolCallResolutionRequest(toolCall),
                agentId,
                channelId,
                supportsVision,
                emitStreamEvents,
                (targetChannelId, jobRequest, innerCt) =>
                    jobService.SubmitAsync(targetChannelId, jobRequest, innerCt),
                (targetAgentId, resourceId, actionKey, innerCt) =>
                    CanSessionUserApproveAsync(
                        targetAgentId,
                        resourceId,
                        innerCt,
                        actionKey),
                (jobId, innerCt) => jobService.CancelAsync(jobId, innerCt),
                approvalCallback,
                (jobId, innerCt) => jobService.ApproveAsync(
                    jobId,
                    new ApproveAgentJobRequest(),
                    innerCt)),
            ct);

    private ChatNativeToolCallResolutionRequest BuildNativeToolCallResolutionRequest(
        ChatToolCall toolCall)
        => new(
            toolCall,
            moduleRegistry,
            moduleExecutionPlanner,
            async (extraction, innerCt) =>
            {
                await using var extractorScope =
                    serviceScopeFactory.CreateAsyncScope();
                return await extraction.Extractor(
                    extractorScope.ServiceProvider,
                    extraction.ArgumentsJson,
                    innerCt);
            },
            message => Debug.WriteLine(message, "SharpClaw.CLI"));

    // ═══════════════════════════════════════════════════════════════
    // Internal types
    // ═══════════════════════════════════════════════════════════════

    private readonly record struct ToolLoopResult(
        string AssistantContent,
        List<AgentJobResponse> JobResults,
        int TotalPromptTokens = 0,
        int TotalCompletionTokens = 0,
        string? ProviderMetadataJson = null);

    private sealed record UserHeaderState(
        string Username,
        string? RoleName,
        IReadOnlyList<string> Grants,
        string? Bio);

    // ═══════════════════════════════════════════════════════════════
    // Tool call notation (persisted in assistant message content)
    // ═══════════════════════════════════════════════════════════════
    //
    // The actual format strings live on ToolNotationFormatter so that
    // tests, clients, and any non-Core call site share one source of
    // truth for the persisted "⚙ [...] → ..." surface.  These thin
    // wrappers exist only to keep the in-file call sites readable.

    private static string FormatInlineToolNotation(string toolName)
        => ToolNotationFormatter.ForInlineTool(toolName);

    private static string FormatTaskToolNotation(string toolName)
        => ToolNotationFormatter.ForTaskTool(toolName);

}
