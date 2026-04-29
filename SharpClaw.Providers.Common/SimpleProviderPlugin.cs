using SharpClaw.Contracts.Providers;

namespace SharpClaw.Providers.Common;

/// <summary>
/// Generic <see cref="IProviderPlugin"/> wrapper used by both the in-Core
/// transitional registrations and per-module plugin classes that don't need
/// custom plugin logic. Each entry passes the provider-specific factory
/// delegate, capability resolver, and (optional) device-code flow.
/// </summary>
public sealed class SimpleProviderPlugin(
    string providerKey,
    string displayName,
    bool requiresEndpoint,
    Func<string?, IProviderApiClient> clientFactory,
    IModelCapabilityResolver capabilities,
    IReadOnlyList<ProviderCostSeed>? costSeeds = null,
    IDeviceCodeFlow? deviceCodeFlow = null,
    IProviderCostFeed? costFeed = null,
    Func<string, string?, string>? agentIdentifierSuffix = null,
    string? ownerModuleId = null) : IProviderPlugin
{
    public string ProviderKey { get; } = providerKey;
    public string DisplayName { get; } = displayName;
    public string OwnerModuleId { get; } = ownerModuleId ?? string.Empty;
    public bool RequiresEndpoint { get; } = requiresEndpoint;
    public IModelCapabilityResolver Capabilities { get; } = capabilities;
    public IReadOnlyList<ProviderCostSeed> CostSeeds { get; } = costSeeds ?? [];
    public IDeviceCodeFlow? DeviceCodeFlow { get; } = deviceCodeFlow;
    public IProviderCostFeed? CostFeed { get; } = costFeed;

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

    public string GetAgentIdentifierSuffix(string providerName, string? sourceUrl)
        => agentIdentifierSuffix is not null
            ? agentIdentifierSuffix(providerName, sourceUrl)
            : providerName.Replace(" ", "-").ToLowerInvariant();
}
