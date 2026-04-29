using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed class ProviderService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    ProviderApiClientFactory clientFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    public async Task<ProviderResponse> CreateAsync(CreateProviderRequest request, CancellationToken ct = default)
    {
        if (request.ProviderKey == WellKnownProviderKeys.Custom && string.IsNullOrWhiteSpace(request.ApiEndpoint))
            throw new ArgumentException("ApiEndpoint is required for custom providers.");

        if (IsUniqueProviderNamesEnforced())
            await EnsureProviderNameUniqueAsync(request.Name, excludeId: null, ct);

        var storeEndpoint = request.ProviderKey is WellKnownProviderKeys.Custom or WellKnownProviderKeys.Ollama
            ? request.ApiEndpoint
            : null;

        var provider = new ProviderDB
        {
            Name = request.Name,
            ProviderKey = request.ProviderKey,
            ApiEndpoint = storeEndpoint,
            EncryptedApiKey = request.ApiKey is not null
                ? encryptionOptions.EncryptProviderKeys
                    ? ApiKeyEncryptor.Encrypt(request.ApiKey, encryptionOptions.Key)
                    : request.ApiKey
                : null
        };

        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);

        return new ProviderResponse(provider.Id, provider.Name, provider.ProviderKey, provider.ApiEndpoint, provider.EncryptedApiKey is not null);
    }

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

        if (request.Name is not null)
        {
            if (IsUniqueProviderNamesEnforced() && !request.Name.Trim().Equals(provider.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                await EnsureProviderNameUniqueAsync(request.Name, excludeId: id, ct);
            provider.Name = request.Name;
        }
        if (request.ApiEndpoint is not null) provider.ApiEndpoint = request.ApiEndpoint;

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
                $"Provider type '{provider.ProviderKey}' does not support device code authentication.");

        using var httpClient = httpClientFactory.CreateClient();
        return await deviceCodeFlow.StartAsync(httpClient, ct);
    }

    private bool IsUniqueProviderNamesEnforced()
    {
        var value = configuration["UniqueNames:Providers"];
        return value is null || !bool.TryParse(value, out var enforced) || enforced;
    }

    private async Task EnsureProviderNameUniqueAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim();
        var names = await db.Providers
            .Where(p => excludeId == null || p.Id != excludeId)
            .Select(p => p.Name)
            .ToListAsync(ct);
        if (names.Any(n => n.Trim().Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A provider named '{name}' already exists.");
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
                $"Provider type '{provider.ProviderKey}' does not support device code authentication.");

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
    /// Returns true if the given provider type supports device code authentication.
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
            var tags = resolver.Resolve(model.Name);
            var tagsRaw = tags.Count > 0 ? string.Join(',', tags) : null;
            if (model.CapabilityTagsRaw != tagsRaw)
            {
                model.CapabilityTagsRaw = tagsRaw;
                updated++;
            }
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

        if (string.IsNullOrEmpty(provider.EncryptedApiKey)
            && provider.ProviderKey != WellKnownProviderKeys.Ollama)
            throw new InvalidOperationException("Provider does not have an API key configured.");

        var apiKey = string.IsNullOrEmpty(provider.EncryptedApiKey)
            ? string.Empty
            : ApiKeyEncryptor.DecryptOrPassthrough(provider.EncryptedApiKey, encryptionOptions.Key);
        var plugin = clientFactory.GetPlugin(provider.ProviderKey)
            ?? throw new ProviderUnavailableException(provider.ProviderKey);
        var client = plugin.CreateClient(provider.ApiEndpoint);

        using var httpClient = httpClientFactory.CreateClient();
        var modelIds = await client.ListModelIdsAsync(httpClient, apiKey, ct);

        var existingNames = provider.Models.Select(m => m.Name).ToHashSet();
        var newModels = modelIds
            .Where(id => !existingNames.Contains(id))
            .Select(id =>
            {
                var tags = plugin.Capabilities.Resolve(id);
                return new ModelDB
                {
                    Name = id,
                    ProviderId = provider.Id,
                    CapabilityTagsRaw = tags.Count > 0 ? string.Join(',', tags) : null
                };
            })
            .ToList();

        if (newModels.Count > 0)
        {
            db.Models.AddRange(newModels);
            await db.SaveChangesAsync(ct);
        }

        return await db.Models
            .Where(m => m.ProviderId == providerId)
            .Select(m => new ModelResponse(m.Id, m.Name, m.ProviderId, provider.Name,
                m.CustomId, m.CapabilityTags))
            .ToListAsync(ct);
    }
}
