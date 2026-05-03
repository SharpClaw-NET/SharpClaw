namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Provides read-only model and provider information needed to start
/// transcription or other inference jobs.
/// Implemented host-side; injected into modules that must resolve a
/// model's API key and provider type without touching Core directly.
/// </summary>
public interface IModelInfoProvider
{
    /// <summary>
    /// Returns the information required to call a model's provider API.
    /// </summary>
    /// <param name="modelId">The model to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ModelProviderInfo"/> record, or <see langword="null"/>
    /// if the model does not exist.
    /// </returns>
    Task<ModelProviderInfo?> GetModelProviderInfoAsync(
        Guid modelId, CancellationToken ct = default);
}

/// <summary>
/// Resolved model + provider information required for inference.
/// </summary>
/// <param name="ModelName">The model name / identifier string to pass to the API.</param>
/// <param name="ProviderKey">The provider key that owns the model.</param>
/// <param name="DecryptedApiKey">Decrypted API key, or empty for local models.</param>
public sealed record ModelProviderInfo(
    string ModelName,
    string ProviderKey,
    string DecryptedApiKey);
