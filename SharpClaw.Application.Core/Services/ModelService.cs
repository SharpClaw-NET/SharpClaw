using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Core.Providers;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ModelService(
    SharpClawDbContext db,
    ModelCatalogEngine modelCatalog,
    IConfiguration configuration)
{
    public async Task<ModelResponse> CreateAsync(CreateModelRequest request, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([request.ProviderId], ct)
            ?? throw new ArgumentException($"Provider {request.ProviderId} not found.");

        if (IsUniqueModelNamesEnforced())
            await EnsureModelNameUniqueAsync(request.Name, excludeId: null, ct);

        var model = modelCatalog.Create(request, provider);

        db.Models.Add(model);
        await db.SaveChangesAsync(ct);

        return modelCatalog.ToResponse(model, provider);
    }

    public async Task<IReadOnlyList<ModelResponse>> ListAsync(Guid? providerId = null, CancellationToken ct = default)
    {
        var query = db.Models
            .Include(m => m.Provider)
            .AsQueryable();

        if (providerId is not null)
            query = query.Where(m => m.ProviderId == providerId);

        return await query
            .Select(modelCatalog.ToResponseProjection())
            .ToListAsync(ct);
    }

    public async Task<ModelResponse?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var m = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == id, ct);
        return m is null ? null : modelCatalog.ToResponse(m);
    }

    public async Task<ModelResponse?> UpdateAsync(Guid id, UpdateModelRequest request, CancellationToken ct = default)
    {
        var model = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null) return null;

        var enforceUniqueNames = IsUniqueModelNamesEnforced();
        var nameExists = request.Name is not null && enforceUniqueNames
            ? await ModelNameExistsAsync(request.Name, excludeId: id, ct)
            : false;

        modelCatalog.ApplyUpdate(
            model,
            request,
            enforceUniqueNames,
            nameExists);

        await db.SaveChangesAsync(ct);
        return modelCatalog.ToResponse(model);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var model = await db.Models.FindAsync([id], ct);
        if (model is null) return false;

        db.Models.Remove(model);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private bool IsUniqueModelNamesEnforced()
    {
        return ModelCatalogEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Models"]);
    }

    private async Task EnsureModelNameUniqueAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        modelCatalog.EnsureModelNameAvailable(
            name,
            await ModelNameExistsAsync(name, excludeId, ct));
    }

    private async Task<bool> ModelNameExistsAsync(
        string name,
        Guid? excludeId,
        CancellationToken ct)
    {
        return await db.Models.AnyAsync(
            m => m.Name == name && (excludeId == null || m.Id != excludeId), ct);
    }
}
