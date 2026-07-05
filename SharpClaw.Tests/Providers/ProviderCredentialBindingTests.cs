using System.Text.Json;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Tests.Providers;

[TestFixture]
public sealed class ProviderCredentialBindingTests
{
    [Test]
    public void CreateClient_WhenCredentialIsRequiredAndMissing_ThrowsLocalConfigurationError()
    {
        var plugin = new BoundPlugin(requiresApiKey: true);

        var act = () => ProviderCredentialBinding.CreateClient(
            plugin,
            ProviderClientOptions.Empty,
            " ");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Provider 'test' requires credentials, but no credentials are configured.");
    }

    [Test]
    public void CreateClient_WhenCredentialIsRequiredAndPluginCannotBind_ThrowsLocalConfigurationError()
    {
        var plugin = new EndpointOnlyPlugin(requiresApiKey: true);

        var act = () => ProviderCredentialBinding.CreateClient(
            plugin,
            ProviderClientOptions.Empty,
            "sk-test");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Provider 'test' requires credentials, but its plugin does not support host-side credential binding.");
    }

    [Test]
    public void CreateClient_WhenCredentialIsRequiredAndPluginCanBind_PassesCredentialToAdapterOwnedSurface()
    {
        var plugin = new BoundPlugin(requiresApiKey: true);

        var client = ProviderCredentialBinding.CreateClient(
            plugin,
            ProviderClientOptions.Empty,
            "sk-test");

        client.Should().BeSameAs(plugin.BoundClient);
        plugin.BoundCredential.Should().Be("sk-test");
        plugin.EndpointOnlyCalls.Should().Be(0);
    }

    [Test]
    public void CreateClient_WhenCredentialIsNotRequired_UsesEndpointOnlyContractsSurface()
    {
        var plugin = new EndpointOnlyPlugin(requiresApiKey: false);

        var client = ProviderCredentialBinding.CreateClient(
            plugin,
            ProviderClientOptions.Empty,
            "ignored");

        client.Should().BeSameAs(plugin.Client);
        plugin.EndpointOnlyCalls.Should().Be(1);
    }

    [Test]
    public void CreateCostFeed_WhenCredentialIsRequiredAndPluginCanBind_PassesCredentialToAdapterOwnedSurface()
    {
        var plugin = new BoundPlugin(requiresApiKey: true, supportsCostFeed: true);

        var feed = ProviderCredentialBinding.CreateCostFeed(
            plugin,
            ProviderClientOptions.Empty,
            "sk-cost");

        feed.Should().BeSameAs(plugin.BoundCostFeed);
        plugin.BoundCredential.Should().Be("sk-cost");
        plugin.EndpointOnlyCostFeedCalls.Should().Be(0);
    }

    [Test]
    public void ApplicationBinder_WhenCredentialIsRequiredAndPluginCannotBind_ThrowsLocalConfigurationError()
    {
        var plugin = new EndpointOnlyPlugin(requiresApiKey: true);

        var act = () => ProviderCredentialBinder.CreateClient(
            plugin,
            ProviderClientOptions.Empty,
            "sk-test");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Provider 'test' requires credentials, but its plugin does not support host-side credential binding.");
    }

    [Test]
    public void ApplicationBinder_WhenCredentialIsRequiredAndPluginCanBind_PassesCredentialByProviderOwnedOverload()
    {
        var plugin = new BoundPlugin(requiresApiKey: true);

        var client = ProviderCredentialBinder.CreateClient(
            plugin,
            ProviderClientOptions.Empty,
            "sk-app");

        client.Should().BeSameAs(plugin.BoundClient);
        plugin.BoundCredential.Should().Be("sk-app");
        plugin.EndpointOnlyCalls.Should().Be(0);
    }

    private sealed class EndpointOnlyPlugin(bool requiresApiKey) : IProviderPlugin
    {
        public RecordingClient Client { get; } = new("test");
        public int EndpointOnlyCalls { get; private set; }

        public string ProviderKey => "test";
        public string DisplayName => "Test";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => requiresApiKey;
        public IModelCapabilityResolver Capabilities { get; } = new Capabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;

        public IProviderApiClient CreateClient(ProviderClientOptions options)
        {
            EndpointOnlyCalls++;
            return Client;
        }
    }

    private sealed class BoundPlugin(
        bool requiresApiKey,
        bool supportsCostFeed = false)
        : IProviderPlugin, IProviderCredentialBoundPlugin
    {
        public RecordingClient EndpointOnlyClient { get; } = new("endpoint-only");
        public RecordingClient BoundClient { get; } = new("bound");
        public RecordingCostFeed? BoundCostFeed { get; } =
            supportsCostFeed ? new RecordingCostFeed() : null;
        public string? BoundCredential { get; private set; }
        public int EndpointOnlyCalls { get; private set; }
        public int EndpointOnlyCostFeedCalls { get; private set; }

        public string ProviderKey => "test";
        public string DisplayName => "Test";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => requiresApiKey;
        public bool SupportsCostFeed => supportsCostFeed;
        public IModelCapabilityResolver Capabilities { get; } = new Capabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;

        public IProviderApiClient CreateClient(ProviderClientOptions options)
        {
            EndpointOnlyCalls++;
            return EndpointOnlyClient;
        }

        public IProviderCostFeed? CreateCostFeed(ProviderClientOptions options)
        {
            EndpointOnlyCostFeedCalls++;
            return BoundCostFeed;
        }

        public IProviderApiClient CreateClient(
            ProviderClientOptions options,
            string credential)
        {
            BoundCredential = credential;
            return BoundClient;
        }

        public IProviderCostFeed? CreateCostFeed(
            ProviderClientOptions options,
            string credential)
        {
            BoundCredential = credential;
            return BoundCostFeed;
        }
    }

    private sealed class Capabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }

    private sealed record RecordingClient(string ProviderKey) : IProviderApiClient
    {
        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingCostFeed : IProviderCostFeed
    {
        public Task<ProviderCostResult?> GetCostsAsync(
            DateTimeOffset startTime,
            DateTimeOffset? endTime,
            CancellationToken ct = default) =>
            Task.FromResult<ProviderCostResult?>(null);
    }
}
