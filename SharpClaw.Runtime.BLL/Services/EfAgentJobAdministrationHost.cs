using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Jobs;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfAgentJobAdministrationHost(
    SharpClawDbContext db,
    IPersistenceEntityResolver entities,
    ChatCache chatCache,
    AgentJobAdministrationEngine jobs) : IAgentJobAdministrationHost
{
    public async Task<AgentJobDB?> LoadJobAsync(
        Guid jobId,
        CancellationToken ct)
    {
        return await entities.FindAsync<AgentJobDB>(db, jobId, ct);
    }

    public async Task<IReadOnlyList<AgentJobDB>> LoadJobsByIdsAsync(
        IReadOnlyList<Guid> jobIds,
        CancellationToken ct)
    {
        var distinctIds = jobIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
            return [];

        var loaded = await db.AgentJobs
            .Where(job => distinctIds.Contains(job.Id))
            .ToListAsync(ct);
        var byId = loaded.ToDictionary(job => job.Id);

        foreach (var id in distinctIds)
        {
            if (byId.ContainsKey(id))
                continue;

            var job = await entities.FindAsync<AgentJobDB>(db, id, ct);
            if (job is not null)
                byId[id] = job;
        }

        return distinctIds
            .Select(id => byId.GetValueOrDefault(id))
            .Where(job => job is not null)
            .Select(job => job!)
            .ToList();
    }

    public async Task<IReadOnlyList<AgentJobDB>> ListJobsForChannelAsync(
        Guid channelId,
        CancellationToken ct)
    {
        return await entities.QueryAsync<AgentJobDB>(
            db,
            job => job.ChannelId == channelId,
            hint: new PersistenceQueryHint("ChannelId", channelId),
            ct: ct);
    }

    public async Task<IReadOnlyList<AgentJobDB>> ListJobsByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId,
        CancellationToken ct)
    {
        return await entities.QueryAsync<AgentJobDB>(
            db,
            job => job.ActionKey != null
                && job.ActionKey.StartsWith(
                    actionKeyPrefix,
                    StringComparison.OrdinalIgnoreCase)
                && (resourceId == null || job.ResourceId == resourceId),
            ct: ct);
    }

    public bool TryGetCachedJobLogResponses(
        Guid jobId,
        out IReadOnlyList<AgentJobLogResponse>? logs)
    {
        return chatCache.TryGetJobLogs(jobId, out logs);
    }

    public async Task<IReadOnlyList<AgentJobLogEntryDB>> LoadJobLogEntriesAsync(
        Guid jobId,
        CancellationToken ct)
    {
        return await entities.QueryAsync<AgentJobLogEntryDB>(
            db,
            log => log.AgentJobId == jobId,
            hint: new PersistenceQueryHint("AgentJobId", jobId),
            ct: ct);
    }

    public void CacheJobLogResponses(
        Guid jobId,
        IReadOnlyList<AgentJobLogResponse> logs)
    {
        chatCache.SetJobLogs(jobId, logs);
    }

    public async Task SaveAsync(
        IReadOnlyList<AgentJobLogEntryDB> logs,
        CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);

        foreach (var log in logs)
            chatCache.AppendJobLogIfCached(log.AgentJobId, jobs.ToLogResponse(log));
    }
}
