using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.Tools;
using SharpClaw.Core.Tools;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ToolAwarenessSetService(
    SharpClawDbContext db,
    ToolAwarenessSetEngine toolAwareness,
    ToolAwarenessAdministrationEngine administration,
    EfToolAwarenessAdministrationHost administrationHost)
{
    public async Task<ToolAwarenessSetResponse> CreateAsync(
        CreateToolAwarenessSetRequest request,
        CancellationToken ct = default)
    {
        return await administration.CreateAsync(
            request,
            administrationHost,
            ct);
    }

    public async Task<ToolAwarenessSetResponse?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
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
        Guid id,
        UpdateToolAwarenessSetRequest request,
        CancellationToken ct = default)
    {
        return await administration.UpdateAsync(
            id,
            request,
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.DeleteAsync(
            id,
            administrationHost,
            ct);
    }
}
