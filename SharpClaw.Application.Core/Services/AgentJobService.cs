using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Core.Jobs;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;
using SharpClaw.Core.Resources;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages the lifecycle of agent action jobs: submission, permission
/// evaluation, optional approval, execution, and outcome tracking.
/// </summary>
public sealed class AgentJobService(
    SharpClawDbContext db,
    AgentActionService actions,
    SessionService session,
    ModuleRegistry moduleRegistry,
    ModuleToolExecutionPlanner moduleExecutionPlanner,
    ModuleToolPermissionExecutor modulePermissionExecutor,
    ModuleJobToolExecutor moduleJobToolExecutor,
    IServiceScopeFactory serviceScopeFactory,
    IConfiguration configuration,
    ChatCache chatCache,
    AgentJobRuntimeEngine jobRuntime,
    AgentJobAdministrationWorkflowEngine jobWorkflow,
    EfAgentJobAdministrationHost jobAdministrationHost,
    AgentJobAdministrationEngine jobAdministration,
    AgentJobDefaultResourceResolver jobDefaultResources,
    ILogger<AgentJobService> logger) : IAgentJobRuntimeHost
{
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<AgentJobService> _logger = logger;
    private static readonly AsyncLocal<Guid?> CurrentExecutionJob = new();

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Submit a new job.  The channel is the primary anchor — the agent
    /// is inferred from the channel unless explicitly overridden in the
    /// request.  Permission is evaluated immediately:
    /// <list type="bullet">
    ///   <item>Approved → executes inline, returns <see cref="AgentJobStatus.Completed"/>
    ///         or <see cref="AgentJobStatus.Failed"/> (or <see cref="AgentJobStatus.Executing"/>
    ///         for long-running work declared by the module).</item>
    ///   <item>PendingApproval → returns <see cref="AgentJobStatus.AwaitingApproval"/>.</item>
    ///   <item>Denied → returns <see cref="AgentJobStatus.Denied"/>.</item>
    /// </list>
    /// </summary>
    public async Task<AgentJobResponse> SubmitAsync(
        Guid channelId,
        SubmitAgentJobRequest request,
        CancellationToken ct = default)
    {
        return await jobRuntime.SubmitAsync(channelId, request, this, ct);
    }

    /// <summary>
    /// Approve a job that is <see cref="AgentJobStatus.AwaitingApproval"/>.
    /// </summary>
    public async Task<AgentJobResponse?> ApproveAsync(
        Guid jobId,
        ApproveAgentJobRequest request,
        CancellationToken ct = default)
    {
        var job = await jobAdministrationHost.LoadJobAsync(jobId, ct);
        if (job is null) return null;

        return await jobRuntime.ApproveAsync(job, request, this, ct);
    }

    /// <summary>Cancel a job that has not yet completed.</summary>
    public async Task<AgentJobResponse?> CancelAsync(
        Guid jobId, CancellationToken ct = default)
    {
        return await jobWorkflow.CancelAsync(jobId, jobAdministrationHost, ct);
    }

    /// <summary>Stop a long-running job and complete it normally.</summary>
    public async Task<AgentJobResponse?> StopAsync(
        Guid jobId, string? requiredActionPrefix = null, CancellationToken ct = default)
    {
        return await jobWorkflow.StopAsync(
            jobId,
            requiredActionPrefix,
            jobAdministrationHost,
            ct);
    }

    /// <summary>
    /// Pause a long-running job. The job can be resumed later with
    /// <see cref="ResumeAsync"/>.
    /// </summary>
    public async Task<AgentJobResponse?> PauseAsync(
        Guid jobId, CancellationToken ct = default)
    {
        return await jobWorkflow.PauseAsync(jobId, jobAdministrationHost, ct);
    }

    /// <summary>
    /// Resume a previously paused job. Module executors own their resume
    /// semantics and restore any module-specific state using the original
    /// job parameters.
    /// </summary>
    public async Task<AgentJobResponse?> ResumeAsync(
        Guid jobId, CancellationToken ct = default)
    {
        return await jobWorkflow.ResumeAsync(jobId, jobAdministrationHost, ct);
    }

    /// <summary>Retrieve a single job by ID.</summary>
    public async Task<AgentJobResponse?> GetAsync(
        Guid jobId, CancellationToken ct = default)
    {
        return await jobWorkflow.GetAsync(jobId, jobAdministrationHost, ct);
    }

    /// <summary>Retrieve a single job summary by ID without loading logs.</summary>
    public async Task<AgentJobSummaryResponse?> GetSummaryAsync(
        Guid jobId, CancellationToken ct = default)
    {
        return await jobWorkflow.GetSummaryAsync(
            jobId,
            jobAdministrationHost,
            ct);
    }

    /// <summary>List all jobs for a channel, most recent first.</summary>
    public async Task<IReadOnlyList<AgentJobResponse>> ListAsync(
        Guid channelId, CancellationToken ct = default)
    {
        return await jobWorkflow.ListAsync(channelId, jobAdministrationHost, ct);
    }

    /// <summary>
    /// List lightweight summaries for all jobs in a channel, most recent first.
    /// Does not load <c>ResultData</c>, <c>ErrorLog</c>, logs, or segments —
    /// suitable for populating dropdowns or list views without memory pressure.
    /// </summary>
    public async Task<IReadOnlyList<AgentJobSummaryResponse>> ListSummariesAsync(
        Guid channelId, CancellationToken ct = default)
    {
        return await jobWorkflow.ListSummariesAsync(
            channelId,
            jobAdministrationHost,
            ct);
    }

    public async Task<IReadOnlyList<AgentJobResponse>> ListJobsByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId = null,
        CancellationToken ct = default)
    {
        return await jobWorkflow.ListByActionPrefixAsync(
            actionKeyPrefix,
            resourceId,
            jobAdministrationHost,
            ct);
    }

    public async Task<IReadOnlyList<AgentJobSummaryResponse>> ListJobSummariesByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId = null,
        CancellationToken ct = default)
    {
        return await jobWorkflow.ListSummariesByActionPrefixAsync(
            actionKeyPrefix,
            resourceId,
            jobAdministrationHost,
            ct);
    }

    public async Task<bool> JobExistsWithActionPrefixAsync(
        Guid jobId, string actionKeyPrefix, CancellationToken ct = default)
    {
        return await jobWorkflow.JobExistsWithActionPrefixAsync(
            jobId,
            actionKeyPrefix,
            jobAdministrationHost,
            ct);
    }

    /// <summary>Returns the session user ID, or <c>null</c> if not authenticated.</summary>
    public Guid? GetSessionUserId() => session.UserId;

    /// <summary>
    /// Evaluates the permission check for an action without creating a job.
    /// Used by the streaming chat loop to determine whether the session
    /// user has authority to approve an awaiting job inline.
    /// </summary>
    public Task<AgentActionResult> CheckPermissionAsync(
        Guid agentId, Guid? resourceId,
        ActionCaller caller, CancellationToken ct = default,
        string? actionKey = null)
        => DispatchPermissionCheckAsync(agentId, resourceId, caller, ct, actionKey);

    Guid? IAgentJobRuntimeHost.SessionUserId => session.UserId;

    ModuleRegistry IAgentJobRuntimeHost.ModuleRegistry => moduleRegistry;

    async Task<ChannelDB?> IAgentJobRuntimeHost.LoadSubmissionChannelAsync(
        Guid channelId,
        CancellationToken ct)
    {
        return await db.Channels
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents)
            .Include(c => c.AllowedAgents)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
    }

    async Task<ChannelDB?> IAgentJobRuntimeHost.LoadApprovalChannelAsync(
        Guid channelId,
        CancellationToken ct)
    {
        return await db.Channels
            .Include(c => c.AgentContext)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
    }

    Task<Guid?> IAgentJobRuntimeHost.ResolveDefaultResourceIdAsync(
        string? actionKey,
        Guid channelId,
        Guid agentId,
        CancellationToken ct)
        => ResolveDefaultResourceIdAsync(actionKey, channelId, agentId, ct);

    void IAgentJobRuntimeHost.TrackJob(AgentJobDB job)
    {
        db.AgentJobs.Add(job);
    }

    async Task IAgentJobRuntimeHost.SaveAsync(
        IReadOnlyList<AgentJobLogEntryDB> logs,
        CancellationToken ct)
    {
        await jobAdministrationHost.SaveAsync(logs, ct);
    }

    Task<AgentActionResult> IAgentJobRuntimeHost.DispatchPermissionCheckAsync(
        Guid agentId,
        Guid? resourceId,
        ActionCaller caller,
        string? actionKey,
        Guid? channelPermissionSetId,
        Guid? contextPermissionSetId,
        CancellationToken ct)
    {
        return DispatchPermissionCheckAsync(
            agentId,
            resourceId,
            caller,
            ct,
            actionKey,
            channelPermissionSetId,
            contextPermissionSetId);
    }

    Task<bool> IAgentJobRuntimeHost.HasChannelAuthorizationAsync(
        Guid channelId,
        Guid? resourceId,
        PermissionClearance agentClearance,
        Guid? callerUserId,
        string? actionKey,
        CancellationToken ct)
    {
        return HasChannelAuthorizationAsync(
            channelId,
            resourceId,
            agentClearance,
            callerUserId,
            ct,
            actionKey);
    }

    async Task<AgentJobExecutionDispatchResult> IAgentJobRuntimeHost.DispatchExecutionAsync(
        AgentJobDB job,
        Action<string, string> addLog,
        CancellationToken ct)
    {
        using var executionScope = BeginExecutionScope(job.Id);
        return await DispatchExecutionAsync(job, addLog, ct);
    }

    void IAgentJobRuntimeHost.LogLongRunningExecutionStarted(AgentJobDB job)
    {
        _logger.LogInformation(
            "Long-running module job {JobId} for action {ActionKey} started and remains Executing.",
            job.Id,
            job.ActionKey);
    }

    void IAgentJobRuntimeHost.LogExecutionFailed(
        AgentJobDB job,
        Exception exception)
    {
        _logger.LogError(
            exception,
            "Agent job {JobId} for action {ActionKey} failed during execution.",
            job.Id,
            job.ActionKey);
    }

    void IAgentJobRuntimeHost.CacheJobLogs(
        Guid jobId,
        IReadOnlyList<AgentJobLogResponse> logs)
    {
        chatCache.SetJobLogs(jobId, logs);
    }

    Task<AgentJobResponse> IAgentJobRuntimeHost.BuildResponseAsync(
        AgentJobDB job,
        CancellationToken ct)
        => jobWorkflow.BuildResponseAsync(job, jobAdministrationHost, ct);

    // ═══════════════════════════════════════════════════════════════
    // Execution
    // ═══════════════════════════════════════════════════════════════

    private async Task<AgentJobExecutionDispatchResult> DispatchExecutionAsync(
        AgentJobDB job,
        Action<string, string> addLog,
        CancellationToken ct)
    {
        var maxEnvelopeSize = SecureJsonOptions.GetMaxEnvelopeSize(_configuration);
        var plan = moduleExecutionPlanner.BuildPlan(
            job.ActionKey,
            job.ScriptJson,
            maxEnvelopeSize,
            moduleRegistry);

        return await DispatchModuleExecutionAsync(job, plan, addLog, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // MODULE DISPATCH — executes module tool calls via ModuleRegistry
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes a module job from a Core-built execution plan by calling
    /// <see cref="ISharpClawCoreModule.ExecuteToolAsync"/> inside a restricted
    /// <see cref="ModuleServiceScope"/> with a per-manifest timeout.
    /// </summary>
    private async Task<AgentJobExecutionDispatchResult> DispatchModuleExecutionAsync(
        AgentJobDB job,
        ModuleToolExecutionPlan plan,
        Action<string, string> addLog,
        CancellationToken ct)
    {
        var result = await moduleJobToolExecutor.ExecuteAsync(
            new ModuleJobToolExecutionRequest(
                job,
                plan,
                moduleRegistry,
                serviceScopeFactory.CreateScope,
                ModuleHostServiceAccess.BlockedServiceTypes,
                message => addLog(message, JobLogLevels.Info),
                IsStreamingNotSupportedException),
            ct);

        return new AgentJobExecutionDispatchResult(
            result.ResultData,
            result.CompletionBehavior);
    }

    private static bool IsStreamingNotSupportedException(Exception ex) =>
        ex is ForeignModuleProtocolException { StatusCode: HttpStatusCode.NotFound };

    /// <summary>
    /// Permission check for module-provided tool calls.
    /// </summary>
    private async Task<AgentActionResult> DispatchModulePermissionCheckAsync(
        Guid agentId, Guid? resourceId, ActionCaller caller,
        string? actionKey, CancellationToken ct,
        Guid? channelPsId = null, Guid? contextPsId = null)
    {
        return await modulePermissionExecutor.ExecuteAsync(
            new ModuleToolPermissionExecutionRequest(
                actionKey,
                resourceId,
                agentId,
                caller,
                moduleRegistry,
                async (delegateName, targetAgentId, targetResourceId,
                    targetCaller, innerCt) =>
                {
                    var result = actions.TryEvaluateByDelegateNameAsync(
                        delegateName,
                        targetAgentId,
                        targetResourceId,
                        targetCaller,
                        innerCt,
                        channelPsId: channelPsId,
                        contextPsId: contextPsId);
                    return result is null ? null : await result;
                },
                message => Debug.WriteLine(message, "SharpClaw.CLI")),
            ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Permission dispatch
    // ═══════════════════════════════════════════════════════════════

    private Task<AgentActionResult> DispatchPermissionCheckAsync(
        Guid agentId, Guid? resourceId,
        ActionCaller caller, CancellationToken ct,
        string? actionKey = null,
        Guid? channelPsId = null, Guid? contextPsId = null)
    {
        return DispatchModulePermissionCheckAsync(agentId, resourceId, caller, actionKey, ct,
            channelPsId, contextPsId);
    }

    // ═══════════════════════════════════════════════════════════════
    // Default resource resolution
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the default resource ID for a per-resource action.
    /// Priority: channel DefaultResourceSet → context DefaultResourceSet
    /// → channel PermissionSet → context PermissionSet → agent role
    /// PermissionSet.
    /// </summary>
    private async Task<Guid?> ResolveDefaultResourceIdAsync(
        string? actionKey, Guid channelId, Guid agentId,
        CancellationToken ct)
    {
        var resolved = await chatCache.GetOrCreateAsync(
            ChatCache.KeyDefaultResourceResolution(channelId, agentId, actionKey),
            async innerCt => new ResolvedDefaultResourceId(
                await ResolveDefaultResourceIdColdAsync(actionKey, channelId, agentId, innerCt)),
            static _ => 32,
            ct);

        return resolved?.ResourceId;
    }

    private async Task<Guid?> ResolveDefaultResourceIdColdAsync(
        string? actionKey, Guid channelId, Guid agentId,
        CancellationToken ct)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .Include(c => c.AgentContext!).ThenInclude(ctx => ctx.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        // Host loads permission snapshots in Core's fallback order.
        var permissionSetIds = new List<Guid>(3);

        if (ch?.PermissionSetId is { } chPsId)
            permissionSetIds.Add(chPsId);

        if (ch?.AgentContext?.PermissionSetId is { } ctxPsId)
            permissionSetIds.Add(ctxPsId);

        var agent = await db.Agents
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        if (agent?.Role?.PermissionSetId is { } rolePsId)
            permissionSetIds.Add(rolePsId);

        var permissionSetSnapshots = new List<PermissionSetSnapshot>(permissionSetIds.Count);
        if (permissionSetIds.Count > 0)
        {
            var permissionSets = await db.PermissionSets
                .Where(p => permissionSetIds.Contains(p.Id))
                .Include(p => p.ResourceAccesses)
                .ToListAsync(ct);

            foreach (var psId in permissionSetIds)
            {
                var ps = permissionSets.FirstOrDefault(p => p.Id == psId);
                if (ps is not null)
                    permissionSetSnapshots.Add(PermissionSetSnapshot.FromPermissionSet(ps));
            }
        }

        return jobDefaultResources.ResolveDefaultResource(
            new AgentJobDefaultResourceResolutionRequest(
                actionKey,
                moduleRegistry,
                ch?.DefaultResourceSet is { } channelDefaults
                    ? DefaultResourceSetSnapshot.FromDefaultResourceSet(channelDefaults)
                    : null,
                ch?.AgentContext?.DefaultResourceSet is { } contextDefaults
                    ? DefaultResourceSetSnapshot.FromDefaultResourceSet(contextDefaults)
                    : null,
                permissionSetSnapshots));
    }

    // ═══════════════════════════════════════════════════════════════
    // Channel / context pre-authorisation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether the channel (or its parent context) has a
    /// user-defined permission set that pre-authorises the requested
    /// action.
    /// <para>
    /// Channel pre-auth provides
    /// <see cref="PermissionClearance.ApprovedByWhitelistedUser"/>-level
    /// authority — the user who configured the channel/context PS is
    /// treated as a whitelisted user granting approval in advance.
    /// </para>
    /// <para>
    /// <b>Level 1 (<see cref="PermissionClearance.ApprovedBySameLevelUser"/>):</b>
    /// the channel PS must contain the grant <b>and</b> the session
    /// user (<paramref name="callerUserId"/>) must personally hold the
    /// same permission (with any non-<see cref="PermissionClearance.Unset"/>
    /// clearance) via their own role.
    /// </para>
    /// <para>
    /// <b>Levels 2 and 4:</b> the channel/context PS grant alone
    /// is sufficient — no additional user check is needed.
    /// </para>
    /// <para>
    /// <b>Level 3 (<see cref="PermissionClearance.ApprovedByPermittedAgent"/>):</b>
    /// agent-only — channel pre-auth is never accepted.
    /// </para>
    /// </summary>
    private async Task<bool> HasChannelAuthorizationAsync(
        Guid channelId,
        Guid? resourceId,
        PermissionClearance agentClearance,
        Guid? callerUserId,
        CancellationToken ct,
        string? actionKey = null)
    {
        var requiresCallerGrant =
            jobAdministration.RequiresCallerGrantForChannelPreauthorization(agentClearance);
        var callerHasGrant = !requiresCallerGrant;

        if (requiresCallerGrant && callerUserId is { } userId)
        {
            var user = await db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user?.Role?.PermissionSetId is { } userPsId)
            {
                var userPs = await actions.LoadPermissionSetAsync(userPsId, ct);
                callerHasGrant = userPs is not null
                    && jobAdministration.HasMatchingGrant(
                        moduleRegistry,
                        userPs,
                        resourceId,
                        actionKey);
            }
        }

        var terminalDecision = jobAdministration.EvaluateChannelPreauthorization(
            agentClearance,
            callerHasGrant,
            channelHasGrant: false,
            contextHasGrant: false);
        if (terminalDecision.Source is AgentJobChannelPreauthorizationSource.NotApplicable
            or AgentJobChannelPreauthorizationSource.CallerGrantMissing)
            return false;

        var ch = await db.Channels
            .Include(c => c.AgentContext)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        var channelHasGrant = false;
        var contextHasGrant = false;

        if (ch?.PermissionSetId is { } chPsId)
        {
            var chPs = await actions.LoadPermissionSetAsync(chPsId, ct);
            channelHasGrant = chPs is not null
                && jobAdministration.HasMatchingGrant(
                    moduleRegistry,
                    chPs,
                    resourceId,
                    actionKey);
        }

        if (ch?.AgentContext?.PermissionSetId is { } ctxPsId)
        {
            var ctxPs = await actions.LoadPermissionSetAsync(ctxPsId, ct);
            contextHasGrant = ctxPs is not null
                && jobAdministration.HasMatchingGrant(
                    moduleRegistry,
                    ctxPs,
                    resourceId,
                    actionKey);
        }

        return jobAdministration.EvaluateChannelPreauthorization(
                agentClearance,
                callerHasGrant,
                channelHasGrant,
                contextHasGrant)
            .IsPreauthorized;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private sealed record ResolvedDefaultResourceId(Guid? ResourceId);

    /// <summary>
    /// Records prompt/completion tokens on a set of jobs that were
    /// submitted during a single LLM round.  Tokens are split evenly
    /// across the jobs; any remainder is assigned to the first job.
    /// </summary>
    public async Task RecordTokensAsync(
        IReadOnlyList<Guid> jobIds, int promptTokens, int completionTokens,
        CancellationToken ct = default)
    {
        await jobWorkflow.RecordTokensAsync(
            jobIds,
            promptTokens,
            completionTokens,
            jobAdministrationHost,
            ct);
    }

    internal async Task RecordTokensForCurrentExecutionAsync(
        int promptTokens, int completionTokens, CancellationToken ct = default)
    {
        if (promptTokens <= 0 && completionTokens <= 0) return;
        if (CurrentExecutionJob.Value is not { } jobId) return;

        await RecordTokensAsync([jobId], promptTokens, completionTokens, ct);
    }

    internal static Guid? CurrentExecutionJobId => CurrentExecutionJob.Value;

    internal static IDisposable BeginExecutionScope(Guid jobId)
    {
        var previous = CurrentExecutionJob.Value;
        CurrentExecutionJob.Value = jobId;
        return new ExecutionScope(previous);
    }

    private sealed class ExecutionScope(Guid? previous) : IDisposable
    {
        public void Dispose() => CurrentExecutionJob.Value = previous;
    }
}
