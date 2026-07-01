using Microsoft.EntityFrameworkCore;
using SharpClaw.Core.Tools;
using SharpClaw.Contracts.DTOs.Tools;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ToolAwarenessSetService(
    SharpClawDbContext db,
    ToolAwarenessSetEngine toolAwareness,
    ChatRuntimeInvalidationPlanner invalidations,
    ChatCache chatCache)
{
    public async Task<ToolAwarenessSetResponse> CreateAsync(
        CreateToolAwarenessSetRequest request, CancellationToken ct = default)
    {
        var entity = toolAwareness.Create(request);

        db.ToolAwarenessSets.Add(entity);
        await db.SaveChangesAsync(ct);
        return toolAwareness.ToResponse(entity);
    }

    public async Task<ToolAwarenessSetResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.ToolAwarenessSets.FindAsync([id], ct);
        return entity is null ? null : toolAwareness.ToResponse(entity);
    }

    public async Task<IReadOnlyList<ToolAwarenessSetResponse>> ListAsync(
        CancellationToken ct = default)
    {
        var sets = await db.ToolAwarenessSets
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        return sets.Select(toolAwareness.ToResponse).ToList();
    }

    public async Task<ToolAwarenessSetResponse?> UpdateAsync(
        Guid id, UpdateToolAwarenessSetRequest request, CancellationToken ct = default)
    {
        var entity = await db.ToolAwarenessSets.FindAsync([id], ct);
        if (entity is null) return null;

        toolAwareness.ApplyUpdate(entity, request);

        await db.SaveChangesAsync(ct);
        invalidations.ToolAwarenessSetsChanged().ApplyTo(chatCache);
        return toolAwareness.ToResponse(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.ToolAwarenessSets.FindAsync([id], ct);
        if (entity is null) return false;

        db.ToolAwarenessSets.Remove(entity);
        await db.SaveChangesAsync(ct);
        invalidations.ToolAwarenessSetsChanged().ApplyTo(chatCache);
        return true;
    }

}
