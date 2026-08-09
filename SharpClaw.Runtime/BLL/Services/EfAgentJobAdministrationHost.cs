using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Jobs;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfAgentJobAdministrationHost(
    SharpClawDbContext db,
    IPersistenceEntityResolver entities,
    DurableExecutionPersistence durablePersistence) : IAgentJobAdministrationHost
{
    public async Task<AgentJobState?> LoadJobAsync(
        Guid jobId,
        CancellationToken ct)
    {
        var entity = await entities.FindAsync<AgentJobDB>(db, jobId, ct);
        return entity is null ? null : ExecutionStateMapper.ToCoreState(entity);
    }

    public async Task<IReadOnlyList<AgentJobState>> LoadJobsByIdsAsync(
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
            .Select(job => ExecutionStateMapper.ToCoreState(job!))
            .ToList();
    }

    public async Task PersistDecisionAsync(
        AgentJobState job,
        AgentJobLifecycleDecision decision,
        CancellationToken ct)
    {
        var entity = await FindTrackedEntityAsync(job.Id, ct)
            ?? throw new InvalidOperationException(
                $"Agent job {job.Id} was not tracked by the Runtime host.");
        ExecutionStateMapper.Apply(job, entity);
        await durablePersistence.PersistJobDecisionAsync(entity, decision, ct);
    }

    public async Task PersistStatesAsync(
        IReadOnlyList<AgentJobState> jobs,
        CancellationToken ct)
    {
        foreach (var state in jobs)
        {
            var entity = await FindTrackedEntityAsync(state.Id, ct)
                ?? throw new InvalidOperationException(
                    $"Agent job {state.Id} was not found while persisting state.");
            ExecutionStateMapper.Apply(state, entity);
        }

        await durablePersistence.SaveJobStateAsync(ct);
    }

    private async Task<AgentJobDB?> FindTrackedEntityAsync(
        Guid id,
        CancellationToken ct)
    {
        return db.AgentJobs.Local.FirstOrDefault(job => job.Id == id)
            ?? await entities.FindAsync<AgentJobDB>(db, id, ct);
    }
}
