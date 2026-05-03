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
    SharpClawDbContext db) : IAgentJobController
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
        string level = "Info",
        CancellationToken ct = default)
    {
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        job.LogEntries.Add(new AgentJobLogEntryDB
        {
            AgentJobId = jobId,
            Message = message,
            Level = level,
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task MarkJobFailedAsync(
        Guid jobId,
        Exception exception,
        CancellationToken ct = default)
    {
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        job.Status = AgentJobStatus.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.ErrorLog = exception.ToString();
        job.LogEntries.Add(new AgentJobLogEntryDB
        {
            AgentJobId = jobId,
            Message = $"Job failed: {exception.Message}",
            Level = "Error",
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task CancelStaleJobsByActionPrefixAsync(
        string actionKeyPrefix,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actionKeyPrefix))
            throw new ArgumentException("Action key prefix is required.", nameof(actionKeyPrefix));

        var stale = await db.AgentJobs
            .Where(j => (j.Status == AgentJobStatus.Executing || j.Status == AgentJobStatus.Queued)
                && j.ActionKey != null
                && j.ActionKey.StartsWith(actionKeyPrefix, StringComparison.OrdinalIgnoreCase))
            .ToListAsync(ct);

        foreach (var job in stale)
        {
            job.Status = AgentJobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.LogEntries.Add(new AgentJobLogEntryDB
            {
                AgentJobId = job.Id,
                Message = "Cancelled: stale from previous session.",
                Level = "Warning",
            });
        }

        if (stale.Count > 0)
            await db.SaveChangesAsync(ct);
    }
}
