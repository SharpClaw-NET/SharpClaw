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
}
