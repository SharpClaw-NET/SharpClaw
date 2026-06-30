using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Entities.Core.Messages;
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
    IPersistenceEntityResolver entities,
    AgentActionService actions,
    SessionService session,
    ModuleRegistry moduleRegistry,
    ModuleToolExecutionPlanner moduleExecutionPlanner,
    ModuleToolPermissionPlanner modulePermissionPlanner,
    ModuleJobToolExecutor moduleJobToolExecutor,
    ModuleEventDispatcher eventDispatcher,
    IServiceScopeFactory serviceScopeFactory,
    IConfiguration configuration,
    ChatCache chatCache,
    AgentJobLifecycleEngine lifecycle,
    AgentJobAdministrationEngine jobAdministration,
    AgentJobDefaultResourceResolver jobDefaultResources,
    ILogger<AgentJobService> logger)
{
    private readonly ModuleEventDispatcher _eventDispatcher = eventDispatcher;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<AgentJobService> _logger = logger;
    private readonly List<AgentJobLogEntryDB> _pendingCacheLogs = [];
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
        var ch = await db.Channels
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents)
            .Include(c => c.AllowedAgents)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct)
            ?? throw new InvalidOperationException($"Channel {channelId} not found.");

        var agentId = jobAdministration.ResolveSubmissionAgent(
            ch,
            channelId,
            request.AgentId);

        var effectiveResourceId = request.ResourceId;

        // When no resource is specified for a per-resource action, resolve
        // the default from: channel DefaultResourceSet → context DefaultResourceSet
        // → channel/context/role PermissionSet defaults.
        if (!effectiveResourceId.HasValue
            && jobAdministration.IsPerResourceAction(moduleRegistry, request.ActionKey))
        {
            effectiveResourceId = await ResolveDefaultResourceIdAsync(
                request.ActionKey, channelId, agentId, ct);
        }

        var job = jobAdministration.CreateSubmissionJob(
            channelId,
            agentId,
            request,
            session.UserId,
            effectiveResourceId);

        db.AgentJobs.Add(job);
        ApplyLifecycleDecision(job, lifecycle.Queue(request.ActionKey));
        await SaveChangesAndCacheLogsAsync(ct);

        var caller = new ActionCaller(session.UserId, request.CallerAgentId);
        var result = await DispatchPermissionCheckAsync(
            agentId, job.ResourceId, caller, ct, job.ActionKey,
            channelPsId: ch.PermissionSetId, contextPsId: ch.AgentContext?.PermissionSetId);

        job.EffectiveClearance = result.EffectiveClearance;

        var channelPreauthorized = result.Verdict == ClearanceVerdict.PendingApproval
            && await HasChannelAuthorizationAsync(
                channelId,
                job.ResourceId,
                result.EffectiveClearance,
                session.UserId,
                ct,
                job.ActionKey);
        var submissionDecision = lifecycle.ResolveSubmissionPermission(
            result,
            channelPreauthorized);
        ApplyLifecycleDecision(job, submissionDecision);

        if (submissionDecision.ShouldExecute)
            await ExecuteJobAsync(job, ct);
        else
            await SaveChangesAndCacheLogsAsync(ct);

        chatCache.SetJobLogs(job.Id, ToLogResponses(job.LogEntries));
        return ToResponse(job);
    }

    /// <summary>
    /// Approve a job that is <see cref="AgentJobStatus.AwaitingApproval"/>.
    /// </summary>
    public async Task<AgentJobResponse?> ApproveAsync(
        Guid jobId,
        ApproveAgentJobRequest request,
        CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        if (job.Status != AgentJobStatus.AwaitingApproval)
        {
            ApplyLifecycleDecision(job, lifecycle.RejectApprovalForStatus(job.Status));
            await SaveChangesAndCacheLogsAsync(ct);
            return await ToResponseAsync(job, ct);
        }

        var approver = new ActionCaller(session.UserId, request.ApproverAgentId);

        var approvalCh = await db.Channels
            .Include(c => c.AgentContext)
            .FirstOrDefaultAsync(c => c.Id == job.ChannelId, ct);

        var result = await DispatchPermissionCheckAsync(
            job.AgentId, job.ResourceId, approver, ct, job.ActionKey,
            channelPsId: approvalCh?.PermissionSetId,
            contextPsId: approvalCh?.AgentContext?.PermissionSetId);

        var approvalDecision = lifecycle.ResolveApproval(
            result,
            approver,
            DateTimeOffset.UtcNow);
        if (approvalDecision.ShouldExecute)
        {
            job.ApprovedByUserId = session.UserId;
            job.ApprovedByAgentId = request.ApproverAgentId;
        }

        ApplyLifecycleDecision(job, approvalDecision);

        if (approvalDecision.ShouldExecute)
            await ExecuteJobAsync(job, ct);
        else
            await SaveChangesAndCacheLogsAsync(ct);

        return await ToResponseAsync(job, ct);
    }

    /// <summary>Cancel a job that has not yet completed.</summary>
    public async Task<AgentJobResponse?> CancelAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        ApplyLifecycleDecision(job, lifecycle.Cancel(job.Status, DateTimeOffset.UtcNow));
        await SaveChangesAndCacheLogsAsync(ct);

        return await ToResponseAsync(job, ct);
    }

    /// <summary>Stop a long-running job and complete it normally.</summary>
    public async Task<AgentJobResponse?> StopAsync(
        Guid jobId, string? requiredActionPrefix = null, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        ApplyLifecycleDecision(
            job,
            lifecycle.Stop(
                job.Status,
                job.ActionKey,
                requiredActionPrefix,
                DateTimeOffset.UtcNow));
        await SaveChangesAndCacheLogsAsync(ct);

        return await ToResponseAsync(job, ct);
    }

    /// <summary>
    /// Pause a long-running job. The job can be resumed later with
    /// <see cref="ResumeAsync"/>.
    /// </summary>
    public async Task<AgentJobResponse?> PauseAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        ApplyLifecycleDecision(job, lifecycle.Pause(job.Status));
        await SaveChangesAndCacheLogsAsync(ct);

        return await ToResponseAsync(job, ct);
    }

    /// <summary>
    /// Resume a previously paused job. Module executors own their resume
    /// semantics and restore any module-specific state using the original
    /// job parameters.
    /// </summary>
    public async Task<AgentJobResponse?> ResumeAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        ApplyLifecycleDecision(job, lifecycle.Resume(job.Status));
        await SaveChangesAndCacheLogsAsync(ct);

        return await ToResponseAsync(job, ct);
    }

    /// <summary>Retrieve a single job by ID.</summary>
    public async Task<AgentJobResponse?> GetAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        return job is null ? null : await ToResponseAsync(job, ct);
    }

    /// <summary>Retrieve a single job summary by ID without loading logs.</summary>
    public async Task<AgentJobSummaryResponse?> GetSummaryAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        return job is null ? null : ToSummaryResponse(job);
    }

    /// <summary>List all jobs for a channel, most recent first.</summary>
    public async Task<IReadOnlyList<AgentJobResponse>> ListAsync(
        Guid channelId, CancellationToken ct = default)
    {
        var jobs = await entities.QueryAsync<AgentJobDB>(
            db,
            j => j.ChannelId == channelId,
            hint: new PersistenceQueryHint("ChannelId", channelId),
            ct: ct);

        var responses = new List<AgentJobResponse>(jobs.Count);
        foreach (var job in jobAdministration.OrderMostRecent(jobs))
            responses.Add(await ToResponseAsync(job, ct));

        return responses;
    }

    /// <summary>
    /// List lightweight summaries for all jobs in a channel, most recent first.
    /// Does not load <c>ResultData</c>, <c>ErrorLog</c>, logs, or segments —
    /// suitable for populating dropdowns or list views without memory pressure.
    /// </summary>
    public async Task<IReadOnlyList<AgentJobSummaryResponse>> ListSummariesAsync(
        Guid channelId, CancellationToken ct = default)
    {
        var jobs = await entities.QueryAsync<AgentJobDB>(
            db,
            j => j.ChannelId == channelId,
            hint: new PersistenceQueryHint("ChannelId", channelId),
            ct: ct);

        return jobAdministration
            .OrderMostRecent(jobs)
            .Select(ToSummaryResponse)
            .ToList();
    }

    public async Task<IReadOnlyList<AgentJobResponse>> ListJobsByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId = null,
        CancellationToken ct = default)
    {
        var jobs = await entities.QueryAsync<AgentJobDB>(
            db,
            jobAdministration.BuildActionPrefixPredicate(actionKeyPrefix, resourceId),
            ct: ct);

        var responses = new List<AgentJobResponse>(jobs.Count);
        foreach (var job in jobAdministration.OrderMostRecent(jobs))
            responses.Add(await ToResponseAsync(job, ct));

        return responses;
    }

    public async Task<IReadOnlyList<AgentJobSummaryResponse>> ListJobSummariesByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId = null,
        CancellationToken ct = default)
    {
        var jobs = await entities.QueryAsync<AgentJobDB>(
            db,
            jobAdministration.BuildActionPrefixPredicate(actionKeyPrefix, resourceId),
            ct: ct);

        return jobAdministration
            .OrderMostRecent(jobs)
            .Select(ToSummaryResponse)
            .ToList();
    }

    public async Task<bool> JobExistsWithActionPrefixAsync(
        Guid jobId, string actionKeyPrefix, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        return jobAdministration.JobMatchesActionPrefix(job, actionKeyPrefix);
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

    // ═══════════════════════════════════════════════════════════════
    // Execution
    // ═══════════════════════════════════════════════════════════════

    private async Task ExecuteJobAsync(AgentJobDB job, CancellationToken ct)
    {
        using var executionScope = BeginExecutionScope(job.Id);

        ApplyLifecycleDecision(job, lifecycle.BeginExecution(DateTimeOffset.UtcNow));
        await SaveChangesAndCacheLogsAsync(ct);

        try
        {
            var execution = await DispatchExecutionAsync(job, ct);
            var completionDecision = lifecycle.CompleteExecution(
                execution.ResultData,
                execution.CompletionBehavior,
                DateTimeOffset.UtcNow);
            ApplyLifecycleDecision(job, completionDecision);

            if (execution.CompletionBehavior == ModuleJobCompletionBehavior.RemainExecuting)
            {
                _logger.LogInformation(
                    "Long-running module job {JobId} for action {ActionKey} started and remains Executing.",
                    job.Id, job.ActionKey);
            }
        }
        catch (Exception ex)
        {
            ApplyLifecycleDecision(
                job,
                lifecycle.FailExecution(
                    ex.Message,
                    ex.ToString(),
                    DateTimeOffset.UtcNow));
            _logger.LogError(ex,
                "Agent job {JobId} for action {ActionKey} failed during execution.",
                job.Id, job.ActionKey);
        }

        await SaveChangesAndCacheLogsAsync(ct);
    }

    private async Task<AgentJobExecutionOutcome> DispatchExecutionAsync(AgentJobDB job, CancellationToken ct)
    {
        var maxEnvelopeSize = SecureJsonOptions.GetMaxEnvelopeSize(_configuration);
        var plan = moduleExecutionPlanner.BuildPlan(
            job.ActionKey,
            job.ScriptJson,
            maxEnvelopeSize,
            moduleRegistry);

        return await DispatchModuleExecutionAsync(job, plan, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // MODULE DISPATCH — executes module tool calls via ModuleRegistry
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes a module job from a Core-built execution plan by calling
    /// <see cref="ISharpClawCoreModule.ExecuteToolAsync"/> inside a restricted
    /// <see cref="ModuleServiceScope"/> with a per-manifest timeout.
    /// </summary>
    private async Task<AgentJobExecutionOutcome> DispatchModuleExecutionAsync(
        AgentJobDB job,
        ModuleToolExecutionPlan plan,
        CancellationToken ct)
    {
        var result = await moduleJobToolExecutor.ExecuteAsync(
            new ModuleJobToolExecutionRequest(
                job,
                plan,
                moduleRegistry,
                serviceScopeFactory.CreateScope,
                ModuleHostServiceAccess.BlockedServiceTypes,
                message => AddLog(job, message),
                IsStreamingNotSupportedException),
            ct);

        return new AgentJobExecutionOutcome(
            result.ResultData,
            result.CompletionBehavior);
    }

    private static bool IsStreamingNotSupportedException(Exception ex) =>
        ex is ForeignModuleProtocolException { StatusCode: HttpStatusCode.NotFound };

    /// <summary>
    /// Permission check for module-provided tool calls.
    /// Resolves the module tool's <see cref="ModuleToolPermission"/> descriptor
    /// and evaluates it:
    /// <list type="bullet">
    ///   <item>If <see cref="ModuleToolPermission.Check"/> is set, calls it directly.</item>
    ///   <item>If <see cref="ModuleToolPermission.DelegateTo"/> is set, routes to the
    ///         named <see cref="AgentActionService"/> method via the delegation map.</item>
    ///   <item>Otherwise, the action is denied (no permission descriptor = no access).</item>
    /// </list>
    /// </summary>
    private async Task<AgentActionResult> DispatchModulePermissionCheckAsync(
        Guid agentId, Guid? resourceId, ActionCaller caller,
        string? actionKey, CancellationToken ct,
        Guid? channelPsId = null, Guid? contextPsId = null)
    {
        var plan = modulePermissionPlanner.BuildPlan(
            actionKey,
            resourceId,
            moduleRegistry);

        if (plan.Kind == ModuleToolPermissionPlanKind.Denied)
        {
            if (plan.DenialReason == ModuleToolPermissionDenialReason.MissingResourceId)
            {
                Debug.WriteLine(
                    $"[PermissionCheck] DENIED: ResourceId is null for per-resource tool '{actionKey}'",
                    "SharpClaw.CLI");
            }

            return plan.DeniedResult
                ?? AgentActionResult.Denied("Module permission plan denied without a reason.");
        }

        Debug.WriteLine(
            $"[PermissionCheck] Tool='{plan.ActionKey}' AgentId={agentId} ResourceId={resourceId} DelegateTo='{plan.DelegateTo}'",
            "SharpClaw.CLI");

        if (plan.Kind == ModuleToolPermissionPlanKind.DirectCheck)
        {
            if (plan.DirectCheck is null)
                throw new InvalidOperationException(
                    "Module permission plan requested direct check without a callback.");

            return await plan.DirectCheck(agentId, resourceId, caller, ct);
        }

        if (plan.Kind == ModuleToolPermissionPlanKind.DelegateToHost)
        {
            if (string.IsNullOrWhiteSpace(plan.DelegateTo))
                throw new InvalidOperationException(
                    "Module permission plan requested host delegate without a delegate name.");

            var result = actions.TryEvaluateByDelegateNameAsync(
                plan.DelegateTo, agentId, resourceId, caller, ct,
                channelPsId: channelPsId, contextPsId: contextPsId);
            if (result is not null) return await result;

            return plan.CreateUnrecognizedDelegateDeniedResult();
        }

        throw new InvalidOperationException(
            $"Unsupported module permission plan kind '{plan.Kind}'.");
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

    private async Task<AgentJobDB?> LoadJobAsync(Guid jobId, CancellationToken ct)
        => await entities.FindAsync<AgentJobDB>(db, jobId, ct);

    private void ApplyLifecycleDecision(
        AgentJobDB job,
        AgentJobLifecycleDecision decision)
    {
        _pendingCacheLogs.AddRange(
            jobAdministration.ApplyLifecycleDecision(job, decision));
    }

    private void AddLog(AgentJobDB job, string message, string level = JobLogLevels.Info)
    {
        _pendingCacheLogs.Add(jobAdministration.AddLog(job, message, level));
    }

    private async Task SaveChangesAndCacheLogsAsync(CancellationToken ct)
    {
        var pendingLogs = _pendingCacheLogs.ToArray();
        await db.SaveChangesAsync(ct);
        _pendingCacheLogs.Clear();

        foreach (var log in pendingLogs)
            chatCache.AppendJobLogIfCached(log.AgentJobId, ToLogResponse(log));
    }

    private sealed record AgentJobExecutionOutcome(
        string? ResultData,
        ModuleJobCompletionBehavior CompletionBehavior);

    private async Task<AgentJobResponse> ToResponseAsync(AgentJobDB job, CancellationToken ct)
        => ToResponse(job, await GetJobLogsAsync(job, ct));

    private async Task<IReadOnlyList<AgentJobLogResponse>> GetJobLogsAsync(
        AgentJobDB job,
        CancellationToken ct)
    {
        if (chatCache.TryGetJobLogs(job.Id, out var cached))
            return cached ?? [];

        var logs = await chatCache.GetJobLogsAsync(
            job.Id,
            async innerCt =>
            {
                var entries = await entities.QueryAsync<AgentJobLogEntryDB>(
                    db,
                    l => l.AgentJobId == job.Id,
                    hint: new PersistenceQueryHint("AgentJobId", job.Id),
                    ct: innerCt);

                return ToLogResponses(entries);
            },
            ct);

        return logs ?? [];
    }

    private AgentJobResponse ToResponse(AgentJobDB job)
        => jobAdministration.ToResponse(job);

    private AgentJobResponse ToResponse(
        AgentJobDB job,
        IReadOnlyList<AgentJobLogResponse> logs)
        => jobAdministration.ToResponse(job, logs);

    private IReadOnlyList<AgentJobLogResponse> ToLogResponses(
        IEnumerable<AgentJobLogEntryDB> logs)
        => jobAdministration.ToLogResponses(logs);

    private AgentJobLogResponse ToLogResponse(AgentJobLogEntryDB log)
        => jobAdministration.ToLogResponse(log);

    private AgentJobSummaryResponse ToSummaryResponse(AgentJobDB job)
        => jobAdministration.ToSummaryResponse(job);

    /// <summary>
    /// Records prompt/completion tokens on a set of jobs that were
    /// submitted during a single LLM round.  Tokens are split evenly
    /// across the jobs; any remainder is assigned to the first job.
    /// </summary>
    public async Task RecordTokensAsync(
        IReadOnlyList<Guid> jobIds, int promptTokens, int completionTokens,
        CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return;

        // Jobs being recorded are from the current session (just executed),
        // so they should be in EF. Fall back to cold store + re-attach if
        // a restart happened mid-flight.
        var loadedJobs = await db.AgentJobs
            .Where(j => jobIds.Contains(j.Id))
            .ToListAsync(ct);
        var jobsById = loadedJobs.ToDictionary(j => j.Id);

        if (jobsById.Count < jobIds.Count)
        {
            foreach (var id in jobIds)
            {
                if (jobsById.ContainsKey(id)) continue;

                var job = await entities.FindAsync<AgentJobDB>(db, id, ct);
                if (job is not null)
                    jobsById[id] = job;
            }
        }

        var jobs = jobIds
            .Select(id => jobsById.GetValueOrDefault(id))
            .Where(job => job is not null)
            .Select(job => job!)
            .ToList();

        if (jobs.Count == 0) return;

        jobAdministration.ApplyTokenUsage(jobs, promptTokens, completionTokens);

        await SaveChangesAndCacheLogsAsync(ct);
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
