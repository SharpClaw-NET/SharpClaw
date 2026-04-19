using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Infrastructure.Models;
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
            Capabilities = request.Capabilities,
            CustomId = request.CustomId,
        };

        db.Models.Add(model);
        await db.SaveChangesAsync(ct);

        return new ModelResponse(model.Id, model.Name, provider.Id, provider.Name, model.Capabilities, model.CustomId);
    }

    public async Task<IReadOnlyList<ModelResponse>> ListAsync(Guid? providerId = null, CancellationToken ct = default)
    {
        var query = db.Models
            .Include(m => m.Provider)
            .AsQueryable();

        if (providerId is not null)
            query = query.Where(m => m.ProviderId == providerId);

        return await query
            .Select(m => new ModelResponse(m.Id, m.Name, m.ProviderId, m.Provider.Name, m.Capabilities, m.CustomId))
            .ToListAsync(ct);
    }

    public async Task<ModelResponse?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var m = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == id, ct);
        return m is null ? null : new ModelResponse(m.Id, m.Name, m.ProviderId, m.Provider.Name, m.Capabilities, m.CustomId);
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
        if (request.Capabilities is not null) model.Capabilities = request.Capabilities.Value;
        if (request.CustomId is not null) model.CustomId = request.CustomId;

        await db.SaveChangesAsync(ct);
        return new ModelResponse(model.Id, model.Name, model.ProviderId, model.Provider.Name, model.Capabilities, model.CustomId);
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
