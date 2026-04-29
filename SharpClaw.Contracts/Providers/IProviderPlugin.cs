namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Plugin contract that a provider module contributes to DI. Replaces
/// the fixed <c>IProviderApiClient</c> dictionary previously held by
/// <c>ProviderApiClientFactory</c>. Each plugin owns one provider key
/// end-to-end: its API client, its model-capability rules, its cost
/// seeds, and (optionally) its device-code authentication flow.
/// </summary>
public interface IProviderPlugin
{
    /// <summary>The well-known provider key this plugin handles.</summary>
    string ProviderKey { get; }

    /// <summary>Human-readable display name shown in the UI and CLI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Module ID that owns this plugin, used by
    /// <c>ProviderApiClientFactory</c> to filter out plugins whose owning
    /// module is currently disabled. Returns an empty string when the
    /// plugin is registered outside the module system (no filtering).
    /// </summary>
    string OwnerModuleId => string.Empty;

    /// <summary>
    /// When <see langword="true"/>, <see cref="CreateClient"/> requires a
    /// non-empty endpoint URL (e.g. Custom, Ollama). When
    /// <see langword="false"/>, the endpoint argument is ignored.
    /// </summary>
    bool RequiresEndpoint { get; }

    /// <summary>
    /// Returns the API client for this provider. Plugins with stateless
    /// clients return a cached singleton; endpoint-bound providers
    /// construct a new client per call.
    /// </summary>
    IProviderApiClient CreateClient(string? endpoint);

    /// <summary>Resolves model capability flags for this provider's models.</summary>
    IModelCapabilityResolver Capabilities { get; }

    /// <summary>Cost seeds inserted at startup for new (provider, model) pairs.</summary>
    IReadOnlyList<ProviderCostSeed> CostSeeds { get; }

    /// <summary>
    /// Optional device-code authentication flow. <see langword="null"/>
    /// for providers that authenticate via static API keys.
    /// </summary>
    IDeviceCodeFlow? DeviceCodeFlow { get; }

    /// <summary>
    /// Optional live-cost reporting surface. <see langword="null"/> for
    /// providers that do not expose a billing/usage API.
    /// </summary>
    IProviderCostFeed? CostFeed => null;

    /// <summary>
    /// Computes the provider-shape suffix used when synthesising the
    /// <c>default-{model}-{suffix}</c> agent identifier. The default
    /// implementation slugifies the provider's display name; plugins
    /// that host models from external download sources (e.g. local
    /// GGUFs from HuggingFace vs. direct URLs) override this to return
    /// a stable identifier derived from <paramref name="sourceUrl"/>.
    /// </summary>
    /// <param name="providerName">
    /// Display name of the provider record the model belongs to.
    /// </param>
    /// <param name="sourceUrl">
    /// Optional source URL recorded for the model (only meaningful for
    /// providers that download model artifacts from a URL).
    /// </param>
    string GetAgentIdentifierSuffix(string providerName, string? sourceUrl)
        => providerName.Replace(" ", "-").ToLowerInvariant();
}
