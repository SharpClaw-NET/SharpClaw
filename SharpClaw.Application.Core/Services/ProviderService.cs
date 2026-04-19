using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    public async Task<ProviderResponse> CreateAsync(CreateProviderRequest request, CancellationToken ct = default)
    {
        if (request.ProviderType == ProviderType.Custom && string.IsNullOrWhiteSpace(request.ApiEndpoint))
            throw new ArgumentException("ApiEndpoint is required for custom providers.");

        if (IsUniqueProviderNamesEnforced())
            await EnsureProviderNameUniqueAsync(request.Name, excludeId: null, ct);

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

        if (request.Name is not null)
        {
            if (IsUniqueProviderNamesEnforced())
                await EnsureProviderNameUniqueAsync(request.Name, excludeId: id, ct);
            provider.Name = request.Name;
        }
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

    private bool IsUniqueProviderNamesEnforced()
    {
        var value = configuration["UniqueNames:Providers"];
        return value is null || !bool.TryParse(value, out var enforced) || enforced;
    }

    private async Task EnsureProviderNameUniqueAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var exists = await db.Providers.AnyAsync(
            p => p.Name == name && (excludeId == null || p.Id != excludeId), ct);
        if (exists)
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
        if (name.StartsWith("whisper") || name.StartsWith("ggml-"))
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

        // ── Legacy base / completions-only models ─────────────────
        if (name.StartsWith("babbage") || name.StartsWith("davinci")
            || name.EndsWith("-instruct"))
            return ModelCapability.None;

        // ── Chat models with transcription suffix ─────────────────
        if (name.Contains("transcribe"))
            return ModelCapability.Chat | ModelCapability.Transcription;

        // ── Chat models with TTS suffix ───────────────────────────
        if (name.Contains("-tts"))
            return ModelCapability.Chat | ModelCapability.TextToSpeech;

        // ── Chat models with audio/realtime capabilities ──────────
        // These models handle audio through the chat completions API,
        // NOT the /v1/audio/transcriptions endpoint. They must NOT
        // get the Transcription flag.
        if (name.Contains("audio") || name.Contains("realtime"))
            return ModelCapability.Chat | ModelCapability.TextToSpeech;

        // ── Positive chat-family matching ─────────────────────────
        // Only models from known chat-compatible families get Chat.
        // Unknown models get None to avoid sending them to the wrong
        // endpoint (e.g. completions-only base models).
        if (!IsChatCapable(name))
            return ModelCapability.None;

        var hasVision = IsVisionCapable(name);
        return hasVision
            ? ModelCapability.Chat | ModelCapability.Vision
            : ModelCapability.Chat;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the model name matches a
    /// known family that supports the chat completions API.
    /// Conservative: models that don't match any pattern are assumed
    /// NOT chat-capable (use <c>model add --cap Chat</c> to override).
    /// </summary>
    private static bool IsChatCapable(string name)
    {
        // OpenAI: gpt-3.5-turbo*, gpt-4*, gpt-5*, chatgpt-*, o1*, o3*, o4*
        // gpt-5+ use the Responses API, not /chat/completions — but they ARE chat-capable.
        if (name.StartsWith("gpt-3.5-turbo") || name.StartsWith("gpt-4")
            || name.StartsWith("gpt-5")
            || name.StartsWith("chatgpt-")
            || name.StartsWith("o1") || name.StartsWith("o3") || name.StartsWith("o4"))
            return true;

        // Anthropic: claude-*
        if (name.StartsWith("claude-"))
            return true;

        // Google: gemini-*
        if (name.StartsWith("gemini-"))
            return true;

        // Mistral: mistral*, mixtral*, pixtral*, codestral*, ministral*
        if (name.StartsWith("mistral") || name.StartsWith("mixtral")
            || name.StartsWith("pixtral") || name.StartsWith("codestral")
            || name.StartsWith("ministral"))
            return true;

        // Meta: llama-*, meta-llama*
        if (name.StartsWith("llama-") || name.StartsWith("meta-llama"))
            return true;

        // xAI: grok-*
        if (name.StartsWith("grok-"))
            return true;

        // Other common chat model families
        if (name.StartsWith("deepseek") || name.StartsWith("qwen")
            || name.StartsWith("phi-") || name.StartsWith("command")
            || name.StartsWith("yi-") || name.StartsWith("jamba"))
            return true;

        // Minimax: MiniMax-Text-01, MiniMax-M1, abab6.5s, etc.
        if (name.StartsWith("minimax") || name.StartsWith("abab"))
            return true;

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the model name matches a
    /// well-known vision-capable family. Conservative: only names
    /// that are documented to accept image inputs.
    /// </summary>
    private static bool IsVisionCapable(string name)
    {
        // OpenAI: gpt-4o*, gpt-4-turbo*, gpt-4-vision*, gpt-5*, o1*, o3*, o4*
        if (name.StartsWith("gpt-4o") || name.StartsWith("gpt-4-turbo")
            || name.StartsWith("gpt-4-vision") || name.StartsWith("gpt-5")
            || name.StartsWith("o1") || name.StartsWith("o3") || name.StartsWith("o4"))
            return true;

        // Anthropic: claude-3*, claude-4*
        if (name.StartsWith("claude-3") || name.StartsWith("claude-4"))
            return true;

        // Google: gemini-1.5*, gemini-2*, gemini-pro-vision
        if (name.StartsWith("gemini-1.5") || name.StartsWith("gemini-2")
            || name.StartsWith("gemini-pro-vision"))
            return true;

        // Mistral: pixtral*
        if (name.StartsWith("pixtral"))
            return true;

        // Meta: llama-3.2-*vision*, llama-4*
        if ((name.StartsWith("llama-3.2") && name.Contains("vision"))
            || name.StartsWith("llama-4"))
            return true;

        // xAI: grok-2-vision*, grok-3*
        if (name.StartsWith("grok-2-vision") || name.StartsWith("grok-3"))
            return true;

        // Minimax: MiniMax-VL-*
        if (name.StartsWith("minimax-vl"))
            return true;

        return false;
    }
}
