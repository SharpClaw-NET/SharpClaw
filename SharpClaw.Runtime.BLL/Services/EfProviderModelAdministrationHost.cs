using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Providers;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfProviderModelAdministrationHost(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    ProviderApiClientFactory clientFactory,
    IConfiguration configuration) : IProviderModelAdministrationHost
{
    public bool UniqueProviderNamesEnforced =>
        ProviderCatalogEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Providers"]);

    public bool UniqueModelNamesEnforced =>
        ModelCatalogEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Models"]);

    public IEnumerable<IProviderPlugin> ProviderPlugins => clientFactory.Plugins;

    public IProviderPlugin? GetProviderPlugin(string providerKey)
    {
        return clientFactory.GetPlugin(providerKey);
    }

    public string ProtectProviderSecret(string secret)
    {
        return encryptionOptions.EncryptProviderKeys
            ? ApiKeyEncryptor.Encrypt(secret, encryptionOptions.Key)
            : secret;
    }

    public string UnprotectProviderSecret(string protectedSecret)
    {
        return ApiKeyEncryptor.DecryptOrPassthrough(
            protectedSecret,
            encryptionOptions.Key);
    }

    public async Task<ProviderDB?> LoadProviderAsync(
        Guid providerId,
        CancellationToken ct)
    {
        return await db.Providers.FindAsync([providerId], ct);
    }

    public async Task<ProviderDB?> LoadProviderWithModelsAsync(
        Guid providerId,
        CancellationToken ct)
    {
        return await db.Providers
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct);
    }

    public async Task<ModelDB?> LoadModelAsync(
        Guid modelId,
        CancellationToken ct)
    {
        return await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);
    }

    public async Task<IReadOnlyList<ProviderDB>> ListProvidersAsync(
        CancellationToken ct)
    {
        return await db.Providers
            .OrderBy(provider => provider.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ModelDB>> ListModelsAsync(
        Guid? providerId,
        CancellationToken ct)
    {
        var query = db.Models
            .Include(model => model.Provider)
            .AsQueryable();

        if (providerId is not null)
            query = query.Where(model => model.ProviderId == providerId);

        return await query
            .OrderBy(model => model.Provider.Name)
            .ThenBy(model => model.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ModelDB>> ListModelsForProviderAsync(
        Guid providerId,
        CancellationToken ct)
    {
        return await db.Models
            .Where(m => m.ProviderId == providerId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListProviderNamesAsync(
        Guid? excludeId,
        CancellationToken ct)
    {
        return await db.Providers
            .Where(p => excludeId == null || p.Id != excludeId)
            .Select(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<bool> ModelNameExistsAsync(
        string name,
        Guid? excludeId,
        CancellationToken ct)
    {
        return await db.Models.AnyAsync(
            model => model.Name == name
                && (excludeId == null || model.Id != excludeId),
            ct);
    }

    public async Task<IReadOnlyList<string>> ListProviderModelIdsAsync(
        ProviderDB provider,
        IProviderPlugin plugin,
        CancellationToken ct)
    {
        var apiKey = string.IsNullOrEmpty(provider.EncryptedApiKey)
            ? string.Empty
            : UnprotectProviderSecret(provider.EncryptedApiKey);
        var client = ProviderCredentialBinder.CreateClient(
            plugin,
            new ProviderClientOptions(provider.ApiEndpoint),
            apiKey);
        return await client.ListModelIdsAsync(ct);
    }

    public async Task<DeviceCodeSession> StartDeviceCodeFlowAsync(
        IDeviceCodeFlow deviceCodeFlow,
        CancellationToken ct)
    {
        return await deviceCodeFlow.StartAsync(ct);
    }

    public async Task<string?> PollDeviceCodeFlowAsync(
        IDeviceCodeFlow deviceCodeFlow,
        DeviceCodeSession session,
        CancellationToken ct)
    {
        return await deviceCodeFlow.PollAsync(session, ct);
    }

    public void TrackProvider(ProviderDB provider)
    {
        db.Providers.Add(provider);
    }

    public void TrackModel(ModelDB model)
    {
        db.Models.Add(model);
    }

    public void TrackModels(IReadOnlyList<ModelDB> models)
    {
        db.Models.AddRange(models);
    }

    public void RemoveProvider(ProviderDB provider)
    {
        db.Providers.Remove(provider);
    }

    public void RemoveModel(ModelDB model)
    {
        db.Models.Remove(model);
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
    }
}
