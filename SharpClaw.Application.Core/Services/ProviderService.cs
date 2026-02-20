using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed class ProviderService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    ProviderApiClientFactory clientFactory,
    IHttpClientFactory httpClientFactory)
{
    public async Task<ProviderResponse> CreateAsync(CreateProviderRequest request, CancellationToken ct = default)
    {
        if (request.ProviderType == ProviderType.Custom && string.IsNullOrWhiteSpace(request.ApiEndpoint))
            throw new ArgumentException("ApiEndpoint is required for custom providers.");

        var provider = new ProviderDB
        {
            Name = request.Name,
            ProviderType = request.ProviderType,
            ApiEndpoint = request.ProviderType == ProviderType.Custom ? request.ApiEndpoint : null,
            EncryptedApiKey = request.ApiKey is not null
                ? ApiKeyEncryptor.Encrypt(request.ApiKey, encryptionOptions.Key)
                : null
        };

        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);

        return new ProviderResponse(provider.Id, provider.Name, provider.ProviderType, provider.ApiEndpoint, provider.EncryptedApiKey is not null);
    }

    public async Task<IReadOnlyList<ProviderResponse>> ListAsync(CancellationToken ct = default)
    {
        return await db.Providers
            .Select(p => new ProviderResponse(p.Id, p.Name, p.ProviderType, p.ApiEndpoint, p.EncryptedApiKey != null))
            .ToListAsync(ct);
    }

    public async Task<ProviderResponse?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Providers.FindAsync([id], ct);
        return p is null ? null : new ProviderResponse(p.Id, p.Name, p.ProviderType, p.ApiEndpoint, p.EncryptedApiKey is not null);
    }

    public async Task<ProviderResponse?> UpdateAsync(Guid id, UpdateProviderRequest request, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([id], ct);
        if (provider is null) return null;

        if (request.Name is not null) provider.Name = request.Name;
        if (request.ApiEndpoint is not null) provider.ApiEndpoint = request.ApiEndpoint;

        await db.SaveChangesAsync(ct);
        return new ProviderResponse(provider.Id, provider.Name, provider.ProviderType, provider.ApiEndpoint, provider.EncryptedApiKey is not null);
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

        provider.EncryptedApiKey = ApiKeyEncryptor.Encrypt(apiKey, encryptionOptions.Key);
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

        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);

        if (client is not IDeviceCodeAuthClient authClient)
            throw new InvalidOperationException(
                $"Provider type '{provider.ProviderType}' does not support device code authentication.");

        using var httpClient = httpClientFactory.CreateClient();
        return await authClient.StartDeviceCodeFlowAsync(httpClient, ct);
    }

    /// <summary>
    /// Polls for the device code flow to complete and stores the resulting access token.
    /// </summary>
    public async Task CompleteDeviceCodeFlowAsync(
        Guid providerId, DeviceCodeSession session, CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([providerId], ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);

        if (client is not IDeviceCodeAuthClient authClient)
            throw new InvalidOperationException(
                $"Provider type '{provider.ProviderType}' does not support device code authentication.");

        using var httpClient = httpClientFactory.CreateClient();
        var accessToken = await authClient.PollForAccessTokenAsync(httpClient, session, ct);

        provider.EncryptedApiKey = ApiKeyEncryptor.Encrypt(accessToken, encryptionOptions.Key);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns true if the given provider type supports device code authentication.
    /// </summary>
    public bool SupportsDeviceCodeAuth(ProviderType providerType, string? apiEndpoint = null)
    {
        var client = clientFactory.GetClient(providerType, apiEndpoint);
        return client is IDeviceCodeAuthClient;
    }


    /// <summary>
    /// Re-infers <see cref="ModelCapability"/> for all existing models of a provider
    /// based on their names. Only updates models whose capabilities were never
    /// manually overridden (i.e. still match what inference would produce or are default <c>Chat</c>).
    /// </summary>
    public async Task<int> RefreshCapabilitiesAsync(Guid providerId, CancellationToken ct = default)
    {
        var models = await db.Models
            .Where(m => m.ProviderId == providerId)
            .ToListAsync(ct);

        var updated = 0;
        foreach (var model in models)
        {
            var inferred = InferCapabilities(model.Name);
            if (model.Capabilities != inferred)
            {
                model.Capabilities = inferred;
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

        if (string.IsNullOrEmpty(provider.EncryptedApiKey))
            throw new InvalidOperationException("Provider does not have an API key configured.");

        var apiKey = ApiKeyEncryptor.Decrypt(provider.EncryptedApiKey, encryptionOptions.Key);
        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);

        using var httpClient = httpClientFactory.CreateClient();
        var modelIds = await client.ListModelIdsAsync(httpClient, apiKey, ct);

        var existingNames = provider.Models.Select(m => m.Name).ToHashSet();
        var newModels = modelIds
            .Where(id => !existingNames.Contains(id))
            .Select(id => new ModelDB
            {
                Name = id,
                ProviderId = provider.Id,
                Capabilities = InferCapabilities(id)
            })
            .ToList();

        if (newModels.Count > 0)
        {
            db.Models.AddRange(newModels);
            await db.SaveChangesAsync(ct);
        }

        return await db.Models
            .Where(m => m.ProviderId == providerId)
            .Select(m => new ModelResponse(m.Id, m.Name, m.ProviderId, provider.Name, m.Capabilities))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Infers <see cref="ModelCapability"/> from a model's name using
    /// well-known naming conventions across providers.
    /// </summary>
    internal static ModelCapability InferCapabilities(string modelName)
    {
        var name = modelName.ToLowerInvariant();

        // ── Pure transcription models ─────────────────────────────
        if (name.StartsWith("whisper"))
            return ModelCapability.Transcription;

        // ── Pure embedding models ─────────────────────────────────
        if (name.Contains("embedding") || name.Contains("embed"))
            return ModelCapability.Embedding;

        // ── Pure TTS models ───────────────────────────────────────
        if (name.StartsWith("tts-"))
            return ModelCapability.TextToSpeech;

        // ── Pure image generation models ──────────────────────────
        if (name.StartsWith("dall-e") || name.StartsWith("gpt-image")
            || name.StartsWith("chatgpt-image") || name.StartsWith("sora"))
            return ModelCapability.ImageGeneration;

        // ── Moderation / non-generative ───────────────────────────
        if (name.Contains("moderation"))
            return ModelCapability.None;

        // ── Chat models with transcription suffix ─────────────────
        if (name.Contains("transcribe"))
            return ModelCapability.Chat | ModelCapability.Transcription;

        // ── Chat models with TTS suffix ───────────────────────────
        if (name.Contains("-tts"))
            return ModelCapability.Chat | ModelCapability.TextToSpeech;

        // ── Chat models with audio/realtime capabilities ──────────
        if (name.Contains("audio") || name.Contains("realtime"))
            return ModelCapability.Chat | ModelCapability.Transcription | ModelCapability.TextToSpeech;

        // ── Everything else is a chat model ───────────────────────
        return ModelCapability.Chat;
    }
}
