using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.DefaultPackages.TestHarness;


internal sealed class TestHarnessProviderPlugin(
    string ownerId,
    string providerKey,
    string displayName,
    bool supportsNativeToolCalling,
    TestHarnessState state) : IProviderPlugin, IProviderCredentialBoundPlugin
{
    private readonly TestHarnessCapabilityResolver _capabilities = new();

    public string ProviderKey => providerKey;
    public string DisplayName => displayName;
    public string OwnerId => ownerId;
    public bool RequiresEndpoint => false;
    public bool RequiresApiKey => false;
    public IModelCapabilityResolver Capabilities => _capabilities;
    public IReadOnlyList<ProviderCostSeed> CostSeeds { get; } =
    [
        new(TestHarnessConstants.ModelId, 0.01m, 0.02m)
    ];
    public ICompletionParameterSpec ParameterSpec => ICompletionParameterSpec.Passthrough;
    public IDeviceCodeFlow? DeviceCodeFlow => null;
    public bool SupportsCostFeed => true;
    public string CostFeedPermissionDeniedNote =>
        "The test harness cost reporter was configured to simulate a permission denial.";

    public IProviderApiClient CreateClient(ProviderClientOptions options) =>
        new TestHarnessProviderClient(providerKey, supportsNativeToolCalling, state, string.Empty);

    public IProviderApiClient CreateClient(
        ProviderClientOptions options,
        string credential) =>
        new TestHarnessProviderClient(providerKey, supportsNativeToolCalling, state, credential);

    public IProviderCostFeed? CreateCostFeed(ProviderClientOptions options) =>
        new TestHarnessCostFeed(providerKey, state);

    public IProviderCostFeed? CreateCostFeed(
        ProviderClientOptions options,
        string credential) =>
        new TestHarnessCostFeed(providerKey, state);
}
