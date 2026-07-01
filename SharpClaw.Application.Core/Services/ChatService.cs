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
    IHttpClientFactory httpClientFactory,
    AgentJobService jobService,
    HeaderTagProcessor headerTagProcessor,
    ThreadActivitySignal threadActivity,
    ModuleRegistry moduleRegistry,
    ModuleToolExecutionPlanner moduleExecutionPlanner,
    ChatCache chatCache,
    ChatRequestPlanningEngine chatPlanner,
    ChatDefaultHeaderEngine chatHeaders,
    ChatHeaderGrantFormatter headerGrantFormatter,
    ChatRequestWorkflowEngine chatWorkflow,
    ChatQueryWorkflowEngine chatQueries,
    EfChatQueryHost chatQueryHost,
    ChatToolSelectionEngine chatToolSelection,
    ChatNativeJobToolExecutor chatNativeJobToolExecutor,
    ChatInlineToolExecutor chatInlineToolExecutor,
    ChatNativeToolLoopEngine chatNativeToolLoop,
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

    private readonly SharpClawDbContext _db = db;
    private readonly AgentJobService _jobService = jobService;
    private readonly ThreadActivitySignal _threadActivity = threadActivity;
    private readonly ChatCache _chatCache = chatCache;
    private readonly ChatQueryWorkflowEngine _chatQueries = chatQueries;
    private readonly EfChatQueryHost _chatQueryHost = chatQueryHost;

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

        var channel = await _db.Channels
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
        var workflowHost = new ChatServiceRequestWorkflowHost(this);
        ChatPreparedRequestState? prepared = null;

        try
        {
            var prepareTiming = Stopwatch.StartNew();
            prepared = await chatWorkflow.BeginPreparedRequestAsync(
                new ChatPreparedRequest(
                    channelId,
                    threadId,
                    channel,
                    agent,
                    plan,
                    request),
                workflowHost,
                ct);
            prepareTiming.Stop();
            userMessagePersisted = true;

            var history = prepared.History;
            if (logTiming)
            {
                logger.LogDebug(
                    "Chat request {RequestId} prepared request lifecycle in {PrepareMs}ms. ThreadId={ThreadId} HistoryMessages={HistoryMessages} HistoryChars={HistoryChars} MaxHistoryMessages={MaxHistoryMessages} MaxHistoryCharacters={MaxHistoryCharacters} ElapsedMs={ElapsedMs}",
                    timingRequestId, prepareTiming.ElapsedMilliseconds, threadId,
                    history.Count, history.Sum(m => m.Content.Length),
                    prepared.MaxHistoryMessages, prepared.MaxHistoryCharacters,
                    totalTiming.ElapsedMilliseconds);
            }

            var apiKey = plan.RequiresApiKey
                ? ApiKeyEncryptor.DecryptOrPassthrough(provider.EncryptedApiKey!, encryptionOptions.Key)
                : "local";
            var client = plan.Client;
            var useNativeTools = plan.UseNativeTools;
            var enableTools = plan.EnableTools;
            var systemPrompt = plan.SystemPrompt;
            var completionParams = plan.CompletionParameters;

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

            var completionTiming = Stopwatch.StartNew();
            var completion = await chatWorkflow.PersistCompletedExchangeAsync(
                new ChatCompletedExchange(
                    channelId,
                    threadId,
                    request,
                    agent,
                    prepared.UserMessage,
                    loopResult.AssistantContent,
                    loopResult.JobResults,
                    loopResult.TotalPromptTokens,
                    loopResult.TotalCompletionTokens,
                    loopResult.ProviderMetadataJson),
                workflowHost,
                ct);
            completionTiming.Stop();

            if (logTiming)
            {
                logger.LogDebug(
                    "Chat request {RequestId} persisted assistant exchange in {CompletionPersistMs}ms. AssistantMessageId={AssistantMessageId} ElapsedMs={ElapsedMs}",
                    timingRequestId, completionTiming.ElapsedMilliseconds,
                    completion.AssistantMessage.Id, totalTiming.ElapsedMilliseconds);

                logger.LogDebug(
                    "Chat request {RequestId} completed in {ElapsedMs}ms. ChannelTokens={ChannelTokens} ThreadTokens={ThreadTokens} AgentTokens={AgentTokens}",
                    timingRequestId, totalTiming.ElapsedMilliseconds,
                    completion.Costs.ChannelCost.TotalTokens,
                    completion.Costs.ThreadCost?.TotalTokens,
                    completion.Costs.AgentCost?.TotalTokens);
            }

            return completion.Response;

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

            await chatWorkflow.TryPersistExceptionErrorAsync(
                new ChatExceptionPersistenceRequest(
                    channelId,
                    threadId,
                    request,
                    ex,
                    userMessagePersisted),
                workflowHost,
                ct);
            throw;
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    public async Task<IReadOnlyList<ChatMessageResponse>> GetHistoryAsync(
        Guid channelId, Guid? threadId = null, int limit = 50, CancellationToken ct = default)
        => await _chatQueries.GetHistoryAsync(
            channelId,
            threadId,
            limit,
            _chatQueryHost,
            ct);

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Token cost aggregation
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    public async Task<ChannelCostResponse> GetChannelCostAsync(
        Guid channelId, CancellationToken ct = default)
        => await _chatQueries.GetChannelCostAsync(channelId, _chatQueryHost, ct);

    public async Task<ThreadCostResponse?> GetThreadCostAsync(
        Guid channelId, Guid threadId, CancellationToken ct = default)
        => await _chatQueries.GetThreadCostAsync(
            channelId,
            threadId,
            _chatQueryHost,
            ct);

    /// <summary>
    /// Aggregated token usage for a single agent across all channels,
    /// with per-channel breakdown.
    /// </summary>
    public async Task<AgentCostResponse?> GetAgentCostAsync(
        Guid agentId, CancellationToken ct = default)
        => await _chatQueries.GetAgentCostAsync(agentId, _chatQueryHost, ct);

    private async Task<AgentCostResponse?> GetAgentCostForKnownAgentAsync(
        Guid agentId, string agentName, CancellationToken ct)
        => await _chatQueries.GetKnownAgentCostAsync(
            agentId,
            agentName,
            _chatQueryHost,
            ct);

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Agent resolution
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

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

        var snapshot = await _db.Users
            .Where(u => u.Id == senderUserId.Value)
            .Select(u => new { u.Username, u.RoleId, RoleName = u.Role != null ? u.Role.Name : null })
            .FirstOrDefaultAsync(ct);

        return snapshot is null
            ? (null, null, null)
            : (snapshot.Username, snapshot.RoleId, snapshot.RoleName);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Chat header
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

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
            var userId2 = _jobService.GetSessionUserId();
            return await headerTagProcessor.ExpandAsync(
                customTemplate, channel, agent, clientType, userId2, ct,
                completionParameters, providerKey);
        }

        if (!chatHeaders.ShouldBuildDefaultHeader(_disableDefaultChatHeaders))
            return null;

        // â”€â”€ Task-sourced message: lightweight header, no user lookup â”€â”€
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

        var userId = _jobService.GetSessionUserId();

        // â”€â”€ External user (bot-forwarded message): no DB session â”€â”€â”€â”€â”€
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

        var userState = await _chatCache.GetOrCreateAsync(
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
        var user = await _db.Users
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
            var ps = await _db.PermissionSets
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
        return await _chatCache.GetOrCreateAsync(
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
        var agentWithRole = await _db.Agents
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
                agentPs = await _db.PermissionSets
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

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Streaming chat
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

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

        var channel = await _db.Channels
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
        var workflowHost = new ChatServiceRequestWorkflowHost(this);
        ChatPreparedRequestState? prepared = null;

        try
        {
        var prepareTiming = Stopwatch.StartNew();
        prepared = await chatWorkflow.BeginPreparedRequestAsync(
            new ChatPreparedRequest(
                channelId,
                threadId,
                channel,
                agent,
                plan,
                request),
            workflowHost,
            ct);
        prepareTiming.Stop();
        userMessagePersisted = true;

        var history = prepared.History;
        if (logTiming)
        {
            logger.LogDebug(
                "Streaming chat request {RequestId} prepared request lifecycle in {PrepareMs}ms. ThreadId={ThreadId} HistoryMessages={HistoryMessages} HistoryChars={HistoryChars} MaxHistoryMessages={MaxHistoryMessages} MaxHistoryCharacters={MaxHistoryCharacters} ElapsedMs={ElapsedMs}",
                timingRequestId, prepareTiming.ElapsedMilliseconds, threadId,
                history.Count, history.Sum(m => m.Content.Length),
                prepared.MaxHistoryMessages, prepared.MaxHistoryCharacters,
                totalTiming.ElapsedMilliseconds);
        }

        var apiKey = plan.RequiresApiKey
            ? ApiKeyEncryptor.DecryptOrPassthrough(provider.EncryptedApiKey!, encryptionOptions.Key)
            : "local";
        var client = plan.Client;
        var systemPrompt = plan.SystemPrompt;
        var completionParams = plan.CompletionParameters;

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

        ChatNativeToolStreamingLoopResult? streamingResult = null;
        await foreach (var loopEvent in chatNativeToolLoop.StreamAsync(
            new ChatNativeToolLoopRequest(
                client,
                httpClient,
                apiKey,
                model.Name,
                systemPrompt,
                history,
                agent.Id,
                channelId,
                plan.ModelCapabilityTags,
                maxTokens,
                providerParams,
                completionParams,
                effectiveTools,
                new ChatServiceNativeToolLoopHost(this),
                ct,
                approvalCallback,
                request.TaskContext,
                toolAwareness,
                threadId,
                timingRequestId,
                () => totalTiming.ElapsedMilliseconds,
                MaxToolCallRounds),
            ct))
        {
            switch (loopEvent.Kind)
            {
                case ChatNativeToolStreamingLoopEventKind.TextDelta:
                    if (loopEvent.Text is { } textDelta)
                    {
                        streamedContent.Append(textDelta);
                        yield return ChatStreamEvent.TextDelta(textDelta);
                    }
                    break;
                case ChatNativeToolStreamingLoopEventKind.BufferedText:
                    if (loopEvent.Text is { } bufferedText)
                        streamedContent.Append(bufferedText);
                    break;
                case ChatNativeToolStreamingLoopEventKind.StreamEvent:
                    if (loopEvent.StreamEventValue is not null)
                        yield return loopEvent.StreamEventValue;
                    break;
                case ChatNativeToolStreamingLoopEventKind.Completed:
                    streamingResult = loopEvent.Result
                        ?? throw new InvalidOperationException(
                            "Core streaming loop completed without a result.");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown native chat streaming event kind '{loopEvent.Kind}'.");
            }
        }

        if (streamingResult is null)
            throw new InvalidOperationException(
                "Core streaming loop ended without a completion event.");

        var assistantContent = streamingResult.AssistantContent;
        var totalPromptTokens = streamingResult.TotalPromptTokens;
        var totalCompletionTokens = streamingResult.TotalCompletionTokens;
        var finalProviderMetadataJson = streamingResult.ProviderMetadataJson;
        var providerRound = streamingResult.ProviderRounds;
        var jobResults = streamingResult.JobResults is List<AgentJobResponse> list
            ? list
            : [.. streamingResult.JobResults];

        var completionTiming = Stopwatch.StartNew();
        var completion = await chatWorkflow.PersistCompletedExchangeAsync(
            new ChatCompletedExchange(
                channelId,
                threadId,
                request,
                agent,
                prepared.UserMessage,
                assistantContent,
                jobResults,
                totalPromptTokens,
                totalCompletionTokens,
                finalProviderMetadataJson),
            workflowHost,
            ct);
        assistantMessagePersisted = true;
        completionTiming.Stop();

        if (logTiming)
        {
            logger.LogDebug(
                "Streaming chat request {RequestId} persisted assistant exchange in {CompletionPersistMs}ms. AssistantMessageId={AssistantMessageId} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} AssistantContentChars={AssistantContentChars} JobResults={JobResultCount} ElapsedMs={ElapsedMs}",
                timingRequestId, completionTiming.ElapsedMilliseconds,
                completion.AssistantMessage.Id, totalPromptTokens, totalCompletionTokens,
                assistantContent.Length, jobResults.Count,
                totalTiming.ElapsedMilliseconds);
        }

        streamCompleted = true;
        if (logTiming)
        {
            logger.LogDebug(
                "Streaming chat request {RequestId} completed in {ElapsedMs}ms. ProviderRounds={ProviderRounds} ChannelTokens={ChannelTokens} ThreadTokens={ThreadTokens} AgentTokens={AgentTokens}",
                timingRequestId, totalTiming.ElapsedMilliseconds,
                providerRound,
                completion.Costs.ChannelCost.TotalTokens,
                completion.Costs.ThreadCost?.TotalTokens,
                completion.Costs.AgentCost?.TotalTokens);
        }

        yield return ChatStreamEvent.Complete(completion.Response);

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

            if (!streamCompleted
                && userMessagePersisted
                && !assistantMessagePersisted
                && streamedContent.Length > 0
                && prepared is not null)
            {
                var partial = await chatWorkflow.TryPersistPartialAssistantMessageAsync(
                    new ChatPartialAssistantPersistenceRequest(
                        channelId,
                        threadId,
                        request,
                        agent,
                        streamedContent.ToString(),
                        TotalPromptTokens: null,
                        TotalCompletionTokens: null,
                        ProviderMetadataJson: null),
                    workflowHost,
                    CancellationToken.None);

                if (partial.Succeeded && logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        "Persisted partial assistant message after interrupted stream. ChannelId={ChannelId} ThreadId={ThreadId} AssistantMessageId={AssistantMessageId} ContentChars={ContentChars}",
                        channelId, threadId, partial.Message!.Id,
                        streamedContent.Length);
                }
                else if (!partial.Succeeded)
                {
                    logger.LogWarning(
                        partial.Exception,
                        "Failed to persist partial assistant message after interrupted stream. ChannelId={ChannelId} ThreadId={ThreadId}",
                        channelId,
                        threadId);
                }
            }

            prepared?.Dispose();
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
        => await chatWorkflow.TryPersistPublicErrorAsync(
            new ChatPublicErrorPersistenceRequest(
                channelId,
                threadId,
                request,
                errorMessage),
            new ChatServiceRequestWorkflowHost(this),
            ct);

    /// <summary>
    /// Checks whether the current session user has sufficient authority
    /// to approve the given action â€” i.e. their own permission check
    /// would return <see cref="ClearanceVerdict.Approved"/>.
    /// </summary>
    private async Task<bool> CanSessionUserApproveAsync(
        Guid agentId, Guid? resourceId,
        CancellationToken ct, string? actionKey = null)
    {
        var userId = _jobService.GetSessionUserId();
        if (userId is null) return false;

        var caller = new ActionCaller(UserId: userId);
        var result = await _jobService.CheckPermissionAsync(
            agentId, resourceId, caller, ct, actionKey);

        return result.Verdict == ClearanceVerdict.Approved;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Task-specific tool handling
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

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
            return await _chatCache.GetOrCreateAsync(
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
        IDictionary<ChatInlineToolPermissionCacheKey, AgentActionResult> permissionCache,
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
        _jobService.CheckPermissionAsync(
            check.AgentId,
            resourceId: null,
            new ActionCaller(AgentId: check.AgentId),
            ct,
            actionKey: check.ActionKey);

    private bool IsInlineToolName(string toolName) =>
        moduleRegistry.IsInlineTool(toolName);

    private Task RecordRoundTokenUsageAsync(
        IReadOnlyList<Guid> jobIds,
        int promptTokens,
        int completionTokens,
        CancellationToken ct) =>
        _jobService.RecordTokensAsync(
            jobIds,
            promptTokens,
            completionTokens,
            ct);

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Tool-call loop implementations
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

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
        var effectiveTools = await GetEffectiveToolsAsync(
            taskContext,
            toolAwareness,
            agentId,
            ct);

        var result = await chatNativeToolLoop.RunAsync(
            new ChatNativeToolLoopRequest(
                client,
                httpClient,
                apiKey,
                modelName,
                systemPrompt,
                dbHistory,
                agentId,
                channelId,
                modelCapabilityTags,
                maxCompletionTokens,
                providerParameters,
                completionParameters,
                effectiveTools,
                new ChatServiceNativeToolLoopHost(this),
                ct,
                approvalCallback,
                taskContext,
                toolAwareness,
                threadId,
                timingRequestId,
                () => totalTiming?.ElapsedMilliseconds,
                MaxToolCallRounds));

        return new ToolLoopResult(
            result.AssistantContent,
            result.JobResults is List<AgentJobResponse> list
                ? list
                : [.. result.JobResults],
            result.TotalPromptTokens,
            result.TotalCompletionTokens,
            result.ProviderMetadataJson);
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
                    _jobService.SubmitAsync(targetChannelId, jobRequest, innerCt),
                (targetAgentId, resourceId, actionKey, innerCt) =>
                    CanSessionUserApproveAsync(
                        targetAgentId,
                        resourceId,
                        innerCt,
                        actionKey),
                (jobId, innerCt) => _jobService.CancelAsync(jobId, innerCt),
                approvalCallback,
                (jobId, innerCt) => _jobService.ApproveAsync(
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

    private sealed class ChatServiceRequestWorkflowHost(
        ChatService service) : IChatRequestWorkflowHost
    {
        public async Task<IDisposable?> BeginThreadProcessingAsync(
            Guid threadId,
            string clientType,
            CancellationToken ct)
        {
            var threadLock = await service._threadActivity.AcquireThreadLockAsync(
                threadId,
                ct);
            service._threadActivity.Publish(
                threadId,
                new ThreadActivityEvent(
                    ThreadActivityEventType.Processing,
                    clientType));
            return threadLock;
        }

        public async Task<ChatProviderHistoryResult> LoadProviderThreadHistoryAsync(
            Guid threadId,
            CancellationToken ct) =>
            await service._chatQueries.GetProviderThreadHistoryAsync(
                threadId,
                service._chatQueryHost,
                ct);

        public async Task<string?> BuildChatHeaderAsync(
            ChannelDB channel,
            AgentDB agent,
            ChatRequest request,
            ChatRequestPlan plan,
            CancellationToken ct) =>
            await service.BuildChatHeaderAsync(
                channel,
                agent,
                request.ClientType,
                ct,
                taskContext: request.TaskContext,
                externalUsername: request.ExternalUsername,
                externalDisplayName: request.ExternalDisplayName,
                completionParameters: plan.CompletionParameters,
                providerKey: plan.ProviderKey);

        public Guid? GetSessionUserId() =>
            service._jobService.GetSessionUserId();

        public async Task<ChatSenderSnapshot> LoadSenderSnapshotAsync(
            Guid? senderUserId,
            string? externalDisplayName,
            string? externalUsername,
            CancellationToken ct)
        {
            var snapshot = await service.ResolveUserSenderSnapshotAsync(
                senderUserId,
                externalDisplayName,
                externalUsername,
                ct);
            return new ChatSenderSnapshot(
                snapshot.Username,
                snapshot.RoleId,
                snapshot.RoleName);
        }

        public async Task PersistChatMessagesAsync(
            IReadOnlyList<ChatMessageDB> messages,
            CancellationToken ct)
        {
            service._db.ChatMessages.AddRange(messages);
            await service._db.SaveChangesAsync(ct);
        }

        public async Task<bool> HasUserMessageAsync(
            Guid channelId,
            Guid? threadId,
            string content,
            CancellationToken ct) =>
            await service._db.ChatMessages.AnyAsync(
                m => m.ChannelId == channelId
                    && m.ThreadId == threadId
                    && (m.Origin == MessageOrigin.User
                        || (m.Origin == null && m.Role == ChatRoles.User))
                    && m.Content == content,
                ct);

        public Task RecordTokensForCurrentExecutionAsync(
            int promptTokens,
            int completionTokens,
            CancellationToken ct) =>
            service._jobService.RecordTokensForCurrentExecutionAsync(
                promptTokens,
                completionTokens,
                ct);

        public void RecordAssistantTokens(
            Guid channelId,
            Guid? threadId,
            Guid agentId,
            string agentName,
            int promptTokens,
            int completionTokens) =>
            service._chatCache.RecordAssistantTokens(
                channelId,
                threadId,
                agentId,
                agentName,
                promptTokens,
                completionTokens);

        public void PublishNewMessages(Guid threadId, string clientType) =>
            service._threadActivity.Publish(
                threadId,
                new ThreadActivityEvent(
                    ThreadActivityEventType.NewMessages,
                    clientType));

        public async Task<ChatResponseCostResult> GetResponseCostsAsync(
            Guid channelId,
            Guid? threadId,
            Guid agentId,
            string agentName,
            CancellationToken ct) =>
            await service._chatQueries.GetResponseCostsAsync(
                channelId,
                threadId,
                agentId,
                agentName,
                service._chatQueryHost,
                ct);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Internal types
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    private sealed class ChatServiceNativeToolLoopHost(
        ChatService service) : IChatNativeToolLoopHost
    {
        public bool IsInlineTool(string toolName) =>
            service.IsInlineToolName(toolName);

        public Task<(bool Handled, string? Result)> TryHandleTaskToolAsync(
            ChatToolCall toolCall,
            TaskChatContext? taskContext,
            CancellationToken ct) =>
            service.TryHandleTaskToolAsync(toolCall, taskContext, ct);

        public Task<string> ExecuteInlineToolAsync(
            ChatToolCall toolCall,
            Guid agentId,
            Guid channelId,
            Guid? threadId,
            IDictionary<ChatInlineToolPermissionCacheKey, AgentActionResult> permissionCache,
            CancellationToken ct) =>
            service.HandleInlineModuleToolAsync(
                toolCall,
                agentId,
                channelId,
                threadId,
                permissionCache,
                ct);

        public Task<ChatNativeJobToolExecutionResult> ExecuteNativeJobToolAsync(
            ChatToolCall toolCall,
            Guid agentId,
            Guid channelId,
            bool supportsVision,
            bool emitStreamEvents,
            Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
            CancellationToken ct) =>
            service.ExecuteNativeJobToolAsync(
                toolCall,
                agentId,
                channelId,
                supportsVision,
                emitStreamEvents,
                approvalCallback,
                ct);

        public Task RecordRoundTokenUsageAsync(
            IReadOnlyList<Guid> jobIds,
            int promptTokens,
            int completionTokens,
            CancellationToken ct) =>
            service.RecordRoundTokenUsageAsync(
                jobIds,
                promptTokens,
                completionTokens,
                ct);
    }
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

}
