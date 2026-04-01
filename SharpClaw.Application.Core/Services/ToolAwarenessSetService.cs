using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models;
using SharpClaw.Contracts.DTOs.Tools;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ToolAwarenessSetService(SharpClawDbContext db)
{
    public async Task<ToolAwarenessSetResponse> CreateAsync(
        CreateToolAwarenessSetRequest request, CancellationToken ct = default)
    {
        var entity = new ToolAwarenessSetDB
        {
            Name = request.Name,
            Tools = request.Tools ?? new()
        };

        db.ToolAwarenessSets.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<ToolAwarenessSetResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.ToolAwarenessSets.FindAsync([id], ct);
        return entity is null ? null : ToResponse(entity);
    }

    public async Task<IReadOnlyList<ToolAwarenessSetResponse>> ListAsync(
        CancellationToken ct = default)
    {
        return await db.ToolAwarenessSets
            .OrderBy(t => t.Name)
            .Select(t => new ToolAwarenessSetResponse(
                t.Id, t.Name, t.Tools, t.CreatedAt, t.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<ToolAwarenessSetResponse?> UpdateAsync(
        Guid id, UpdateToolAwarenessSetRequest request, CancellationToken ct = default)
    {
        var entity = await db.ToolAwarenessSets.FindAsync([id], ct);
        if (entity is null) return null;

        if (request.Name is not null) entity.Name = request.Name;
        if (request.Tools is not null) entity.Tools = request.Tools;

        await db.SaveChangesAsync(ct);
        return ToResponse(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.ToolAwarenessSets.FindAsync([id], ct);
        if (entity is null) return false;

        db.ToolAwarenessSets.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static ToolAwarenessSetResponse ToResponse(ToolAwarenessSetDB entity) =>
        new(entity.Id, entity.Name, entity.Tools, entity.CreatedAt, entity.UpdatedAt);
}
