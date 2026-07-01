using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Tools;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class EfToolAwarenessAdministrationHost(
    SharpClawDbContext db,
    ChatCache chatCache) : IToolAwarenessAdministrationHost
{
    public async Task<ToolAwarenessSetDB?> LoadToolAwarenessSetAsync(
        Guid id,
        CancellationToken ct)
    {
        return await db.ToolAwarenessSets.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<ToolAwarenessSetDB>> ListToolAwarenessSetsAsync(
        CancellationToken ct)
    {
        return await db.ToolAwarenessSets
            .OrderBy(set => set.Name)
            .ToListAsync(ct);
    }

    public void TrackToolAwarenessSet(ToolAwarenessSetDB entity)
    {
        db.ToolAwarenessSets.Add(entity);
    }

    public void RemoveToolAwarenessSet(ToolAwarenessSetDB entity)
    {
        db.ToolAwarenessSets.Remove(entity);
    }

    public async Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        buildInvalidationPlan?.Invoke()?.ApplyTo(chatCache);
    }
}
