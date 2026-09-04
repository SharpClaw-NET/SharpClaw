using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.Host;

/// <summary>Registers provider and model rows for registration-owned runtime state.</summary>
internal sealed class RuntimeModelRegistrar(IServiceScopeFactory scopeFactory) : IModelRegistrar
{
    public async Task<Guid> EnsureProviderAsync(
        string providerKey,
        string displayName,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var existing = await db.Providers
            .FirstOrDefaultAsync(provider => provider.ProviderKey == providerKey, ct);
        if (existing is not null)
            return existing.Id;

        var provider = new ProviderDB
        {
            Name = displayName,
            ProviderKey = providerKey,
        };
        db.Providers.Add(provider);
        await db.SaveChangesThroughKernelAsync(ct);
        return provider.Id;
    }

    public async Task<Guid> EnsureModelAsync(
        string modelName,
        Guid providerId,
        IReadOnlyList<string> capabilityTags,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var existing = await db.Models
            .FirstOrDefaultAsync(model => model.Name == modelName && model.ProviderId == providerId, ct);
        if (existing is not null)
            return existing.Id;

        var model = new ModelDB
        {
            Name = modelName,
            ProviderId = providerId,
            CapabilityTagsRaw = capabilityTags.Count == 0 ? null : string.Join(',', capabilityTags),
        };
        db.Models.Add(model);
        await db.SaveChangesThroughKernelAsync(ct);
        return model.Id;
    }

    public async Task<ModelMetadata?> GetModelMetadataAsync(
        Guid modelId,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var model = await db.Models
            .Include(value => value.Provider)
            .FirstOrDefaultAsync(value => value.Id == modelId, ct);
        if (model is null)
            return null;

        return new ModelMetadata(
            model.Name,
            model.ProviderId,
            model.Provider.Name,
            model.Provider.ProviderKey,
            model.CustomId,
            model.CapabilityTags);
    }

    public async Task<bool> DeleteModelAsync(Guid modelId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var model = await db.Models.FirstOrDefaultAsync(value => value.Id == modelId, ct);
        if (model is null)
            return false;

        db.Models.Remove(model);
        await db.SaveChangesThroughKernelAsync(ct);
        return true;
    }
}
