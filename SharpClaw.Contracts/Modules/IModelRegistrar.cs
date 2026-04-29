namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Host→module write adapter exposing a minimal surface for upserting
/// <c>ProviderDB</c> and <c>ModelDB</c> rows. Implemented by the host
/// (<c>HostModelRegistrar</c> in <c>Application.Core.Modules</c>) and
/// consumed by modules that need to register provider/model rows when
/// they own additional state in their own DbContext (e.g. the LlamaSharp
/// module owning <c>LocalModelFileDB</c>).
/// </summary>
public interface IModelRegistrar
{
    /// <summary>
    /// Ensures a provider with the given <paramref name="providerKey"/>
    /// exists, creating one with <paramref name="displayName"/> if missing.
    /// Returns the provider's ID.
    /// </summary>
    Task<Guid> EnsureProviderAsync(
        string providerKey,
        string displayName,
        CancellationToken ct = default);

    /// <summary>
    /// Ensures a model with the given <paramref name="modelName"/> exists
    /// under the given provider, creating one with the supplied capability
    /// tags if missing. Returns the model's ID.
    /// </summary>
    Task<Guid> EnsureModelAsync(
        string modelName,
        Guid providerId,
        IReadOnlyList<string> capabilityTags,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the model name and provider key for a registered model,
    /// or <see langword="null"/> when no model exists with the given id.
    /// </summary>
    Task<ModelMetadata?> GetModelMetadataAsync(
        Guid modelId,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the model row identified by <paramref name="modelId"/>.
    /// Returns <c>true</c> when a row was removed.
    /// </summary>
    Task<bool> DeleteModelAsync(
        Guid modelId,
        CancellationToken ct = default);
}

/// <summary>Metadata describing a registered model row.</summary>
public sealed record ModelMetadata(
    string Name,
    Guid ProviderId,
    string ProviderName,
    string ProviderKey,
    string? CustomId,
    IReadOnlySet<string>? CapabilityTags);
