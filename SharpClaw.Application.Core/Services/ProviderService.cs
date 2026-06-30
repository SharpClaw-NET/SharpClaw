using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Providers;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed class ProviderService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    ProviderApiClientFactory clientFactory,
    ProviderCatalogEngine providerCatalog,
    ModelCatalogEngine modelCatalog,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    public async Task<ProviderResponse> CreateAsync(CreateProviderRequest request, CancellationToken ct = default)
    {
        var plugin = clientFactory.GetPlugin(request.ProviderKey);
        var plan = providerCatalog.PlanCreate(
            request,
            plugin,
            IsUniqueProviderNamesEnforced(),
            await LoadProviderNamesAsync(excludeId: null, ct));

        var provider = new ProviderDB
        {
            Name = plan.Name,
            ProviderKey = plan.ProviderKey,
            ApiEndpoint = plan.ApiEndpointToStore,
            EncryptedApiKey = plan.ApiKey is not null
                ? encryptionOptions.EncryptProviderKeys
                    ? ApiKeyEncryptor.Encrypt(plan.ApiKey, encryptionOptions.Key)
                    : plan.ApiKey
                : null
        };

        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);

        return new ProviderResponse(provider.Id, provider.Name, provider.ProviderKey, provider.ApiEndpoint, provider.EncryptedApiKey is not null);
    }

    public IReadOnlyList<ProviderTypeResponse> ListAvailableTypes()
        => providerCatalog.ListAvailableTypes(clientFactory.Plugins);

    public async Task<IReadOnlyList<ProviderResponse>> ListAsync(CancellationToken ct = default)
    {
        return await db.Providers
            .Select(p => new ProviderResponse(p.Id, p.Name, p.ProviderKey, p.ApiEndpoint, p.EncryptedApiKey != null))
            .ToListAsync(ct);
    }

    public async Task<ProviderResponse?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Providers.FindAsync([id], ct);
        return p is null ? null : new ProviderResponse(p.Id, p.Name, p.ProviderKey, p.ApiEndpoint, p.EncryptedApiKey is not null);
    }

    public async Task<ProviderResponse?> UpdateAsync(Guid id, UpdateProviderRequest request, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([id], ct);
        if (provider is null) return null;

        var plan = providerCatalog.PlanUpdate(
            provider.Name,
            request,
            IsUniqueProviderNamesEnforced(),
            await LoadProviderNamesAsync(excludeId: id, ct));

        provider.Name = plan.Name;
        if (plan.UpdateApiEndpoint)
            provider.ApiEndpoint = plan.ApiEndpoint;

        await db.SaveChangesAsync(ct);
        return new ProviderResponse(provider.Id, provider.Name, provider.ProviderKey, provider.ApiEndpoint, provider.EncryptedApiKey is not null);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([id], ct);
        if (provider is null) return false;

        db.Providers.Remove(provider);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Sets the API key for an existing provider.
    /// </summary>
    public async Task SetApiKeyAsync(Guid providerId, string apiKey, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([providerId], ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        provider.EncryptedApiKey = encryptionOptions.EncryptProviderKeys
            ? ApiKeyEncryptor.Encrypt(apiKey, encryptionOptions.Key)
            : apiKey;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Starts a device code flow for a provider that supports it.
    /// Returns the session containing the user code and verification URI.
    /// </summary>
    public async Task<DeviceCodeSession> StartDeviceCodeFlowAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([providerId], ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        var deviceCodeFlow = clientFactory.GetPlugin(provider.ProviderKey)?.DeviceCodeFlow
            ?? throw new InvalidOperationException(
                $"Provider key '{provider.ProviderKey}' does not support device code authentication.");

        using var httpClient = httpClientFactory.CreateClient();
        return await deviceCodeFlow.StartAsync(httpClient, ct);
    }

    private bool IsUniqueProviderNamesEnforced()
    {
        return ProviderCatalogEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Providers"]);
    }

    private async Task<IReadOnlyList<string>> LoadProviderNamesAsync(
        Guid? excludeId,
        CancellationToken ct)
    {
        return await db.Providers
            .Where(p => excludeId == null || p.Id != excludeId)
            .Select(p => p.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Polls for the device code flow to complete and stores the resulting access token.
    /// </summary>
    public async Task CompleteDeviceCodeFlowAsync(
        Guid providerId, DeviceCodeSession session, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([providerId], ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        var deviceCodeFlow = clientFactory.GetPlugin(provider.ProviderKey)?.DeviceCodeFlow
            ?? throw new InvalidOperationException(
                $"Provider key '{provider.ProviderKey}' does not support device code authentication.");

        using var httpClient = httpClientFactory.CreateClient();
        var accessToken = await deviceCodeFlow.PollAsync(httpClient, session, ct)
            ?? throw new InvalidOperationException(
                $"Device code flow for provider '{provider.ProviderKey}' did not return an access token.");

        provider.EncryptedApiKey = encryptionOptions.EncryptProviderKeys
            ? ApiKeyEncryptor.Encrypt(accessToken, encryptionOptions.Key)
            : accessToken;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns true if the given provider key supports device code authentication.
    /// </summary>
    public bool SupportsDeviceCodeAuth(string providerKey, string? apiEndpoint = null)
        => clientFactory.GetPlugin(providerKey)?.DeviceCodeFlow is not null;


    /// <summary>
    /// Re-infers <see cref="ModelCapability"/> for all existing models of a provider
    /// based on their names. Only updates models whose capabilities were never
    /// manually overridden (i.e. still match what inference would produce or are default <c>Chat</c>).
    /// </summary>
    public async Task<int> RefreshCapabilitiesAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([providerId], ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");
        var resolver = clientFactory.GetPlugin(provider.ProviderKey)?.Capabilities
            ?? throw new ProviderUnavailableException(provider.ProviderKey);

        var models = await db.Models
            .Where(m => m.ProviderId == providerId)
            .ToListAsync(ct);

        var updated = 0;
        foreach (var model in models)
        {
            if (modelCatalog.RefreshCapabilityTags(model, resolver))
                updated++;
        }

        if (updated > 0)
            await db.SaveChangesAsync(ct);

        return updated;
    }

    /// <summary>
    /// Queries the provider's API for available models and upserts them into the database.
    /// </summary>
    public async Task<IReadOnlyList<ModelResponse>> SyncModelsAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await db.Providers
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        var plugin = clientFactory.GetPlugin(provider.ProviderKey);
        providerCatalog.EnsureCanSyncModels(
            provider.ProviderKey,
            !string.IsNullOrEmpty(provider.EncryptedApiKey),
            plugin);
        if (plugin is null)
            throw new ProviderUnavailableException(provider.ProviderKey);

        var apiKey = string.IsNullOrEmpty(provider.EncryptedApiKey)
            ? string.Empty
            : ApiKeyEncryptor.DecryptOrPassthrough(provider.EncryptedApiKey, encryptionOptions.Key);
        var client = plugin.CreateClient(provider.ApiEndpoint);

        using var httpClient = httpClientFactory.CreateClient();
        var modelIds = await client.ListModelIdsAsync(httpClient, apiKey, ct);

        var existingNames = provider.Models.Select(m => m.Name).ToHashSet();
        var newModels = modelCatalog.BuildMissingModels(
            provider.Id,
            modelIds,
            existingNames,
            plugin.Capabilities);

        if (newModels.Count > 0)
        {
            db.Models.AddRange(newModels);
            await db.SaveChangesAsync(ct);
        }

        var models = await db.Models
            .Where(m => m.ProviderId == providerId)
            .ToListAsync(ct);

        return models
            .Select(m => modelCatalog.ToResponse(m, provider))
            .ToList();
    }
}
