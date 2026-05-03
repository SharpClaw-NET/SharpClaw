using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ModelService(SharpClawDbContext db, IConfiguration configuration)
{
    public async Task<ModelResponse> CreateAsync(CreateModelRequest request, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([request.ProviderId], ct)
            ?? throw new ArgumentException($"Provider {request.ProviderId} not found.");

        if (IsUniqueModelNamesEnforced())
            await EnsureModelNameUniqueAsync(request.Name, excludeId: null, ct);

        var model = new ModelDB
        {
            Name = request.Name,
            ProviderId = provider.Id,
            CustomId = request.CustomId,
            CapabilityTagsRaw = request.CapabilityTags is { Count: > 0 }
                ? string.Join(',', request.CapabilityTags)
                : null
        };

        db.Models.Add(model);
        await db.SaveChangesAsync(ct);

        return new ModelResponse(model.Id, model.Name, provider.Id, provider.Name,
            model.CustomId, model.CapabilityTags);
    }

    public async Task<IReadOnlyList<ModelResponse>> ListAsync(Guid? providerId = null, CancellationToken ct = default)
    {
        var query = db.Models
            .Include(m => m.Provider)
            .AsQueryable();

        if (providerId is not null)
            query = query.Where(m => m.ProviderId == providerId);

        return await query
            .Select(m => new ModelResponse(m.Id, m.Name, m.ProviderId, m.Provider.Name,
                m.CustomId, m.CapabilityTags))
            .ToListAsync(ct);
    }

    public async Task<ModelResponse?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var m = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == id, ct);
        return m is null ? null : new ModelResponse(m.Id, m.Name, m.ProviderId, m.Provider.Name,
            m.CustomId, m.CapabilityTags);
    }

    public async Task<ModelResponse?> UpdateAsync(Guid id, UpdateModelRequest request, CancellationToken ct = default)
    {
        var model = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null) return null;

        if (request.Name is not null)
        {
            if (IsUniqueModelNamesEnforced())
                await EnsureModelNameUniqueAsync(request.Name, excludeId: id, ct);
            model.Name = request.Name;
        }
        if (request.CustomId is not null) model.CustomId = request.CustomId;
        if (request.CapabilityTags is not null)
            model.CapabilityTagsRaw = request.CapabilityTags.Count > 0
                ? string.Join(',', request.CapabilityTags)
                : null;

        await db.SaveChangesAsync(ct);
        return new ModelResponse(model.Id, model.Name, model.ProviderId, model.Provider.Name,
            model.CustomId, model.CapabilityTags);
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
        var value = configuration["UniqueNames:Models"];
        return value is null || !bool.TryParse(value, out var enforced) || enforced;
    }

    private async Task EnsureModelNameUniqueAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var exists = await db.Models.AnyAsync(
            m => m.Name == name && (excludeId == null || m.Id != excludeId), ct);
        if (exists)
            throw new InvalidOperationException($"A model named '{name}' already exists.");
    }
}
