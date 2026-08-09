using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Jobs;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Modules;

public sealed class HostAgentJobController(
    AgentJobService jobs,
    SharpClawDbContext db,
    AgentJobAdministrationEngine jobAdministration,
    AgentJobLifecycleEngine jobLifecycle,
    DurableExecutionPersistence persistence) : IAgentJobController
{
    public Task<AgentJobResponse> SubmitJobAsync(
        Guid channelId,
        SubmitAgentJobRequest request,
        CancellationToken ct = default) =>
        jobs.SubmitAsync(channelId, request, ct);

    public Task<AgentJobDetailResponse?> StopJobAsync(
        Guid jobId,
        string? requiredActionPrefix = null,
        CancellationToken ct = default) =>
        jobs.StopAsync(jobId, requiredActionPrefix, ct);

    public async Task AddJobLogAsync(
        Guid jobId,
        string message,
        string level = JobLogLevels.Info,
        CancellationToken ct = default)
    {
        await persistence.AppendJobLogAsync(
            jobId,
            message,
            level,
            "ModuleJobDiagnostic",
            ct);
    }

    public async Task MarkJobFailedAsync(
        Guid jobId,
        Exception exception,
        CancellationToken ct = default)
    {
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        await ApplyDecisionAsync(
            job,
            jobLifecycle.FailModuleCallback(
                job.Status,
                exception.Message,
                exception.ToString(),
                DateTimeOffset.UtcNow),
            ct);
    }

    public async Task MarkJobCompletedAsync(
        Guid jobId,
        string? resultData = null,
        string? message = null,
        CancellationToken ct = default)
    {
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        await ApplyDecisionAsync(
            job,
            jobLifecycle.CompleteModuleCallback(
                job.Status,
                resultData,
                message,
                DateTimeOffset.UtcNow),
            ct);
    }

    public async Task MarkJobCancelledAsync(
        Guid jobId,
        string? message = null,
        CancellationToken ct = default)
    {
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        await ApplyDecisionAsync(
            job,
            jobLifecycle.CancelModuleCallback(
                job.Status,
                message,
                DateTimeOffset.UtcNow),
            ct);
    }

    public async Task CancelStaleJobsByActionPrefixAsync(
        string actionKeyPrefix,
        CancellationToken ct = default)
    {
        jobAdministration.EnsureModuleCallbackActionPrefix(actionKeyPrefix);

        var candidates = await db.AgentJobs
            .Where(j => (j.Status == AgentJobStatus.Executing || j.Status == AgentJobStatus.Queued)
                && j.ActionKey != null)
            .ToListAsync(ct);

        var stale = candidates
            .Where(j => jobAdministration.JobMatchesActionPrefix(
                ExecutionStateMapper.ToCoreState(j),
                actionKeyPrefix))
            .ToList();

        foreach (var job in stale)
        {
            var decision = jobLifecycle.CancelStaleFromPreviousSession(
                job.Status,
                DateTimeOffset.UtcNow);
            if (!decision.HasChanges)
                continue;

            var state = ExecutionStateMapper.ToCoreState(job);
            jobAdministration.ApplyLifecycleState(state, decision);
            ExecutionStateMapper.Apply(state, job);
            await persistence.PersistJobDecisionAsync(job, decision, ct);
        }
    }

    private async Task ApplyDecisionAsync(
        AgentJobDB job,
        AgentJobLifecycleDecision decision,
        CancellationToken ct)
    {
        if (!decision.HasChanges)
            return;

        var state = ExecutionStateMapper.ToCoreState(job);
        jobAdministration.ApplyLifecycleState(state, decision);
        ExecutionStateMapper.Apply(state, job);
        await persistence.PersistJobDecisionAsync(job, decision, ct);
    }
}
