using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Core.Modules;

public sealed class HostAgentJobController(
    AgentJobService jobs,
    SharpClawDbContext db,
    ChatCache chatCache) : IAgentJobController
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

        var entry = new AgentJobLogEntryDB
        {
            AgentJobId = jobId,
            Message = message,
            Level = level,
        };
        job.LogEntries.Add(entry);

        await db.SaveChangesAsync(ct);
        chatCache.AppendJobLogIfCached(jobId, ToLogResponse(entry));
    }

    public async Task MarkJobFailedAsync(
        Guid jobId,
        Exception exception,
        CancellationToken ct = default)
    {
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;
        if (job.Status.IsTerminal()) return;

        job.Status = AgentJobStatus.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.ErrorLog = exception.ToString();
        var entry = new AgentJobLogEntryDB
        {
            AgentJobId = jobId,
            Message = $"Job failed: {exception.Message}",
            Level = JobLogLevels.Error,
        };
        job.LogEntries.Add(entry);

        await db.SaveChangesAsync(ct);
        chatCache.AppendJobLogIfCached(jobId, ToLogResponse(entry));
    }

    public async Task MarkJobCompletedAsync(
        Guid jobId,
        string? resultData = null,
        string? message = null,
        CancellationToken ct = default)
    {
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;
        if (job.Status.IsTerminal()) return;

        job.Status = AgentJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        if (resultData is not null)
            job.ResultData = resultData;

        var entry = new AgentJobLogEntryDB
        {
            AgentJobId = jobId,
            Message = string.IsNullOrWhiteSpace(message)
                ? "Job completed by module."
                : message,
            Level = JobLogLevels.Info,
        };
        job.LogEntries.Add(entry);

        await db.SaveChangesAsync(ct);
        chatCache.AppendJobLogIfCached(jobId, ToLogResponse(entry));
    }

    public async Task MarkJobCancelledAsync(
        Guid jobId,
        string? message = null,
        CancellationToken ct = default)
    {
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;
        if (job.Status.IsTerminal()) return;

        job.Status = AgentJobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        var entry = new AgentJobLogEntryDB
        {
            AgentJobId = jobId,
            Message = string.IsNullOrWhiteSpace(message)
                ? "Job cancelled by module."
                : message,
            Level = JobLogLevels.Warning,
        };
        job.LogEntries.Add(entry);

        await db.SaveChangesAsync(ct);
        chatCache.AppendJobLogIfCached(jobId, ToLogResponse(entry));
    }

    public async Task CancelStaleJobsByActionPrefixAsync(
        string actionKeyPrefix,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actionKeyPrefix))
            throw new ArgumentException("Action key prefix is required.", nameof(actionKeyPrefix));

        var candidates = await db.AgentJobs
            .Where(j => (j.Status == AgentJobStatus.Executing || j.Status == AgentJobStatus.Queued)
                && j.ActionKey != null)
            .ToListAsync(ct);

        var stale = candidates
            .Where(j => j.ActionKey!.StartsWith(actionKeyPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var cancelledLogs = new List<(Guid JobId, AgentJobLogEntryDB Entry)>(stale.Count);
        foreach (var job in stale)
        {
            job.Status = AgentJobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            var entry = new AgentJobLogEntryDB
            {
                AgentJobId = job.Id,
                Message = "Cancelled: stale from previous session.",
                Level = JobLogLevels.Warning,
            };
            job.LogEntries.Add(entry);
            cancelledLogs.Add((job.Id, entry));
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var (jobId, entry) in cancelledLogs)
                chatCache.AppendJobLogIfCached(jobId, ToLogResponse(entry));
        }
    }

    private static AgentJobLogResponse ToLogResponse(AgentJobLogEntryDB log)
        => new(log.Message, log.Level, log.CreatedAt);
}
