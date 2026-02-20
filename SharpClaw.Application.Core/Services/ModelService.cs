using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ModelService(SharpClawDbContext db)
{
    public async Task<ModelResponse> CreateAsync(CreateModelRequest request, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([request.ProviderId], ct)
            ?? throw new ArgumentException($"Provider {request.ProviderId} not found.");

        var model = new ModelDB
        {
            Name = request.Name,
            ProviderId = provider.Id,
            Capabilities = request.Capabilities
        };

        db.Models.Add(model);
        await db.SaveChangesAsync(ct);

        return new ModelResponse(model.Id, model.Name, provider.Id, provider.Name, model.Capabilities);
    }

    public async Task<IReadOnlyList<ModelResponse>> ListAsync(CancellationToken ct = default)
    {
        return await db.Models
            .Include(m => m.Provider)
            .Select(m => new ModelResponse(m.Id, m.Name, m.ProviderId, m.Provider.Name, m.Capabilities))
            .ToListAsync(ct);
    }

    public async Task<ModelResponse?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var m = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == id, ct);
        return m is null ? null : new ModelResponse(m.Id, m.Name, m.ProviderId, m.Provider.Name, m.Capabilities);
    }

    public async Task<ModelResponse?> UpdateAsync(Guid id, UpdateModelRequest request, CancellationToken ct = default)
    {
        var model = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null) return null;

        if (request.Name is not null) model.Name = request.Name;
        if (request.Capabilities is not null) model.Capabilities = request.Capabilities.Value;

        await db.SaveChangesAsync(ct);
        return new ModelResponse(model.Id, model.Name, model.ProviderId, model.Provider.Name, model.Capabilities);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var model = await db.Models.FindAsync([id], ct);
        if (model is null) return false;

        db.Models.Remove(model);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
