using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Jobs;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Core.Modules;

public sealed class HostAgentJobController(
    AgentJobService jobs,
    SharpClawDbContext db,
    ChatCache chatCache,
    AgentJobAdministrationEngine jobAdministration,
    AgentJobLifecycleEngine jobLifecycle) : IAgentJobController
{
    public Task<AgentJobResponse> SubmitJobAsync(
        Guid channelId,
        SubmitAgentJobRequest request,
        CancellationToken ct = default) =>
        jobs.SubmitAsync(channelId, request, ct);

    public Task<AgentJobResponse?> StopJobAsync(
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
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        var entry = jobAdministration.AddLog(job, message, level);

        await db.SaveChangesAsync(ct);
        chatCache.AppendJobLogIfCached(
            jobId,
            jobAdministration.ToLogResponse(entry));
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
            .Where(j => jobAdministration.JobMatchesActionPrefix(j, actionKeyPrefix))
            .ToList();

        var cancelledLogs = new List<(Guid JobId, AgentJobLogEntryDB Entry)>(stale.Count);
        foreach (var job in stale)
        {
            var decision = jobLifecycle.CancelStaleFromPreviousSession(
                job.Status,
                DateTimeOffset.UtcNow);
            if (!decision.HasChanges)
                continue;

            foreach (var entry in jobAdministration.ApplyLifecycleDecision(job, decision))
                cancelledLogs.Add((job.Id, entry));
        }

        if (cancelledLogs.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var (jobId, entry) in cancelledLogs)
            {
                chatCache.AppendJobLogIfCached(
                    jobId,
                    jobAdministration.ToLogResponse(entry));
            }
        }
    }

    private async Task ApplyDecisionAsync(
        AgentJobDB job,
        AgentJobLifecycleDecision decision,
        CancellationToken ct)
    {
        if (!decision.HasChanges)
            return;

        var logs = jobAdministration.ApplyLifecycleDecision(job, decision);
        await db.SaveChangesAsync(ct);

        foreach (var entry in logs)
        {
            chatCache.AppendJobLogIfCached(
                job.Id,
                jobAdministration.ToLogResponse(entry));
        }
    }
}
