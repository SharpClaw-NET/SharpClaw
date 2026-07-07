namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Provides read-only model and provider information needed to start
/// module-owned inference jobs.
/// Implemented host-side; injected into modules that must resolve a
/// model and provider metadata without touching Core directly.
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

    /// <summary>
    /// Returns a ready local model file path for providers that need an on-disk
    /// model file. Hosts without a local file for the model return
    /// <see langword="null"/>.
    /// </summary>
    Task<string?> GetLocalModelFilePathAsync(
        Guid modelId, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Resolved model and provider metadata.
/// </summary>
/// <param name="ModelName">The model name / identifier string to pass to the API.</param>
/// <param name="ProviderKey">The provider key that owns the model.</param>
/// <param name="RequiresApiKey">Whether the registered provider plugin requires an API key.</param>
/// <param name="HasApiKey">Whether the host has a protected API key configured for the provider.</param>
public sealed record ModelProviderInfo(
    string ModelName,
    string ProviderKey,
    bool RequiresApiKey,
    bool HasApiKey);
