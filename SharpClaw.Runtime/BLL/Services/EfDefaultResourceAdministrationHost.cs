using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Resources;
using SharpClaw.Core.State;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfDefaultResourceAdministrationHost(
    SharpClawDbContext db,
    ChatCache chatCache) : IDefaultResourceAdministrationHost
{
    private readonly CoreStateSession _states = new(db);

    public async Task<ChannelState?> LoadChannelWithDefaultResourcesAsync(
        Guid channelId,
        CancellationToken ct)
    {
        var entity = await db.Channels
            .Include(c => c.DefaultResourceSet!)
                .ThenInclude(set => set.Entries)
            .Include(c => c.AgentContext!)
                .ThenInclude(context => context.DefaultResourceSet!)
            .ThenInclude(set => set.Entries)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<ChannelContextState?> LoadContextWithDefaultResourcesAsync(
        Guid contextId,
        CancellationToken ct)
    {
        var entity = await db.AgentContexts
            .Include(c => c.DefaultResourceSet!)
            .ThenInclude(set => set.Entries)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<IReadOnlyList<Guid>> ListChannelIdsForContextAsync(
        Guid contextId,
        CancellationToken ct)
    {
        return await db.Channels
            .Where(c => c.AgentContextId == contextId)
            .Select(c => c.Id)
            .ToListAsync(ct);
    }

    public void TrackDefaultResourceSet(DefaultResourceSetState defaultResourceSet)
    {
        _states.Track(defaultResourceSet);
    }

    public void RemoveDefaultResourceEntry(DefaultResourceEntryState entry)
    {
        _states.Remove(entry);
    }

    public async Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct)
    {
        await _states.SaveChangesAsync(ct);
        buildInvalidationPlan?.Invoke()?.ApplyTo(chatCache);
    }
}
