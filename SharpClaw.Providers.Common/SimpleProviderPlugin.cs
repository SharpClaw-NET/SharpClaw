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
    ICompletionParameterSpec? parameterSpec = null,
    IDeviceCodeFlow? deviceCodeFlow = null,
    IProviderCostFeed? costFeed = null,
    Func<string, Guid, CancellationToken, Task<string>>? agentIdentifierSuffix = null,
    bool supportsAutomaticEndpointDiscovery = false,
    bool isSeedable = true,
    bool requiresApiKey = true,
    string? ownerModuleId = null) : IProviderPlugin
{
    public string ProviderKey { get; } = providerKey;
    public string DisplayName { get; } = displayName;
    public string OwnerModuleId { get; } = ownerModuleId ?? string.Empty;
    public bool RequiresEndpoint { get; } = requiresEndpoint;
    public bool SupportsAutomaticEndpointDiscovery { get; } = supportsAutomaticEndpointDiscovery;
    public bool IsSeedable { get; } = isSeedable;
    public bool RequiresApiKey { get; } = requiresApiKey;
    public IModelCapabilityResolver Capabilities { get; } = capabilities;
    public IReadOnlyList<ProviderCostSeed> CostSeeds { get; } = costSeeds ?? [];
    public ICompletionParameterSpec ParameterSpec { get; } = parameterSpec ?? ICompletionParameterSpec.Passthrough;
    public IDeviceCodeFlow? DeviceCodeFlow { get; } = deviceCodeFlow;
    public IProviderCostFeed? CostFeed { get; } = costFeed;

    public IProviderApiClient CreateClient(string? endpoint)
    {
        if (RequiresEndpoint
            && !SupportsAutomaticEndpointDiscovery
            && string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException(
                $"Provider '{ProviderKey}' requires a non-empty endpoint URL.",
                nameof(endpoint));
        }

        return clientFactory(endpoint);
    }

    public Task<string> GetAgentIdentifierSuffixAsync(
        string providerName, Guid modelId, CancellationToken ct = default)
        => agentIdentifierSuffix is not null
            ? agentIdentifierSuffix(providerName, modelId, ct)
            : Task.FromResult(providerName.Replace(" ", "-").ToLowerInvariant());
}
