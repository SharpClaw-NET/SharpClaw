using SharpClaw.Contracts.Providers;

namespace SharpClaw.Application.Core.Providers;

/// <summary>
/// Generic <see cref="IProviderPlugin"/> wrapper used while every plugin
/// still lives inside Core. Each entry in
/// <c>BuiltInProviderPlugins.Build</c> instantiates one of these with
/// the provider-specific factory delegate, capability resolver, and
/// (optional) device-code flow. Phase 6 onwards replaces each instance
/// with a concrete plugin class inside its owning module.
/// </summary>
public sealed class SimpleProviderPlugin(
    string providerKey,
    string displayName,
    bool requiresEndpoint,
    Func<string?, IProviderApiClient> clientFactory,
    IModelCapabilityResolver capabilities,
    IReadOnlyList<ProviderCostSeed>? costSeeds = null,
    IDeviceCodeFlow? deviceCodeFlow = null) : IProviderPlugin
{
    public string ProviderKey { get; } = providerKey;
    public string DisplayName { get; } = displayName;
    public bool RequiresEndpoint { get; } = requiresEndpoint;
    public IModelCapabilityResolver Capabilities { get; } = capabilities;
    public IReadOnlyList<ProviderCostSeed> CostSeeds { get; } = costSeeds ?? [];
    public IDeviceCodeFlow? DeviceCodeFlow { get; } = deviceCodeFlow;

    public IProviderApiClient CreateClient(string? endpoint)
    {
        if (RequiresEndpoint && string.IsNullOrWhiteSpace(endpoint)
            && ProviderKey == WellKnownProviderKeys.Custom)
        {
            throw new ArgumentException(
                $"Provider '{ProviderKey}' requires a non-empty endpoint URL.",
                nameof(endpoint));
        }

        return clientFactory(endpoint);
    }
}
