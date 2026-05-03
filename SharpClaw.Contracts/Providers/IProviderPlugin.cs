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
    /// When <see langword="true"/>, the provider's client supports
    /// automatic endpoint discovery (e.g. Ollama local-server detection).
    /// Used by <c>ProviderService</c> to decide whether to invoke the
    /// automatic discovery step when no endpoint is explicitly configured.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    bool SupportsAutomaticEndpointDiscovery => false;

    /// <summary>
    /// When <see langword="true"/>, this plugin should be included in the
    /// startup seed data. When <see langword="false"/>, the plugin is
    /// excluded from seeding (typically used for <c>Custom</c> providers
    /// that require user-supplied endpoint configuration). Defaults to
    /// <see langword="true"/>.
    /// </summary>
    bool IsSeedable => true;

    /// <summary>
    /// When <see langword="true"/>, this provider requires an API key to
    /// authenticate requests; <c>ProviderService</c>/<c>ChatService</c>
    /// will fail fast if no key is configured. When <see langword="false"/>
    /// (e.g. local-inference or local HTTP servers), the key check is
    /// skipped and the client is invoked with an empty/sentinel key.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    bool RequiresApiKey => true;

    /// <summary>
    /// Returns the API client for this provider.
    /// clients return a cached singleton; endpoint-bound providers
    /// construct a new client per call.
    /// </summary>
    IProviderApiClient CreateClient(string? endpoint);

    /// <summary>Resolves model capability flags for this provider's models.</summary>
    IModelCapabilityResolver Capabilities { get; }

    /// <summary>Cost seeds inserted at startup for new (provider, model) pairs.</summary>
    IReadOnlyList<ProviderCostSeed> CostSeeds { get; }

    /// <summary>
    /// Completion parameter constraints and supported features. Used by
    /// <c>CompletionParameterValidator</c> and the generated provider
    /// parameter reference documentation. Defaults to a permissive
    /// passthrough spec for unknown/custom providers.
    /// </summary>
    ICompletionParameterSpec ParameterSpec => ICompletionParameterSpec.Passthrough;

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
    /// GGUFs from HuggingFace vs. direct URLs) override this to derive
    /// a stable identifier from module-owned data keyed by the model.
    /// </summary>
    /// <param name="providerName">
    /// Display name of the provider record the model belongs to.
    /// </param>
    /// <param name="modelId">The model identifier being assigned.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GetAgentIdentifierSuffixAsync(
        string providerName, Guid modelId, CancellationToken ct = default)
        => Task.FromResult(providerName.Replace(" ", "-").ToLowerInvariant());
}
