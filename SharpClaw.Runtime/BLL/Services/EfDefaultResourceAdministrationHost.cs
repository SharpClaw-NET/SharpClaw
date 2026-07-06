using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Resources;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfDefaultResourceAdministrationHost(
    SharpClawDbContext db,
    ChatCache chatCache) : IDefaultResourceAdministrationHost
{
    public async Task<ChannelDB?> LoadChannelWithDefaultResourcesAsync(
        Guid channelId,
        CancellationToken ct)
    {
        return await db.Channels
            .Include(c => c.DefaultResourceSet!)
                .ThenInclude(set => set.Entries)
            .Include(c => c.AgentContext!)
                .ThenInclude(context => context.DefaultResourceSet!)
                .ThenInclude(set => set.Entries)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
    }

    public async Task<ChannelContextDB?> LoadContextWithDefaultResourcesAsync(
        Guid contextId,
        CancellationToken ct)
    {
        return await db.AgentContexts
            .Include(c => c.DefaultResourceSet!)
                .ThenInclude(set => set.Entries)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);
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

    public void TrackDefaultResourceSet(DefaultResourceSetDB defaultResourceSet)
    {
        db.DefaultResourceSets.Add(defaultResourceSet);
    }

    public void RemoveDefaultResourceEntry(DefaultResourceEntryDB entry)
    {
        db.DefaultResourceEntries.Remove(entry);
    }

    public async Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        buildInvalidationPlan?.Invoke()?.ApplyTo(chatCache);
    }
}
