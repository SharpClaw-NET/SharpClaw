using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Core.Providers;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ModelService(
    SharpClawDbContext db,
    ModelCatalogEngine modelCatalog,
    ProviderModelAdministrationEngine administration,
    EfProviderModelAdministrationHost administrationHost)
{
    public async Task<ModelResponse> CreateAsync(
        CreateModelRequest request,
        CancellationToken ct = default)
    {
        return await administration.CreateModelAsync(
            request,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<ModelResponse>> ListAsync(
        Guid? providerId = null,
        CancellationToken ct = default)
    {
        var query = db.Models
            .Include(model => model.Provider)
            .AsQueryable();

        if (providerId is not null)
            query = query.Where(model => model.ProviderId == providerId);

        return await query
            .Select(modelCatalog.ToResponseProjection())
            .ToListAsync(ct);
    }

    public async Task<ModelResponse?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        var model = await db.Models
            .Include(model => model.Provider)
            .FirstOrDefaultAsync(model => model.Id == id, ct);

        return model is null ? null : modelCatalog.ToResponse(model);
    }

    public async Task<ModelResponse?> UpdateAsync(
        Guid id,
        UpdateModelRequest request,
        CancellationToken ct = default)
    {
        return await administration.UpdateModelAsync(
            id,
            request,
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await administration.DeleteModelAsync(
            id,
            administrationHost,
            ct);
    }
}
