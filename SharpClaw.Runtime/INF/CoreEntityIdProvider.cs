using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Modules;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.INF;

/// <summary>
/// Infrastructure implementation of <see cref="ICoreEntityIdProvider"/>.
/// Queries the host <see cref="SharpClawDbContext"/> for agent and channel
/// IDs/names so modules can resolve them without referencing Infrastructure.
/// </summary>
internal sealed class CoreEntityIdProvider(SharpClawDbContext db) : ICoreEntityIdProvider
{
    public Task<List<Guid>> GetAgentIdsAsync(CancellationToken ct = default)
        => db.Agents.Select(a => a.Id).ToListAsync(ct);

    public Task<List<Guid>> GetChannelIdsAsync(CancellationToken ct = default)
        => db.Channels.Select(c => c.Id).ToListAsync(ct);

    public Task<List<(Guid Id, string Name)>> GetAgentLookupItemsAsync(CancellationToken ct = default)
        => db.Agents.Select(a => new ValueTuple<Guid, string>(a.Id, a.Name)).ToListAsync(ct);

    public Task<List<(Guid Id, string Name)>> GetChannelLookupItemsAsync(CancellationToken ct = default)
        => db.Channels
            .Select(c => new ValueTuple<Guid, string>(c.Id, c.Title ?? c.Id.ToString()))
            .ToListAsync(ct);
}
