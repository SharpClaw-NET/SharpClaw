using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.Host;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeProviderClientFactoryTests
{
    [Test]
    public void Alternate_provider_never_inherits_default_credentials_or_endpoint()
    {
        var primary = new CredentialProvider("primary");
        var alternate = new CredentialProvider("alternate");
        IProviderPlugin[] plugins = [primary, alternate];
        var factory = new RuntimeProviderClientFactory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "primary",
                ["Provider:ApiKey"] = "primary-secret",
                ["Provider:Endpoint"] = "https://primary.example",
            })
            .Build();

        factory.Create(configuration, plugins, "primary").Should().BeSameAs(primary);
        primary.LastCredential.Should().Be("primary-secret");
        primary.LastEndpoint.Should().Be("https://primary.example");
        Assert.Throws<InvalidOperationException>(() =>
            factory.Create(configuration, plugins, "alternate"));
        alternate.LastCredential.Should().BeNull();
        alternate.LastEndpoint.Should().BeNull();

        var scopedConfiguration = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:alternate:ApiKey"] = "alternate-secret",
                ["Providers:alternate:Endpoint"] = "https://alternate.example",
            })
            .Build();

        factory.Create(scopedConfiguration, plugins, "alternate").Should().BeSameAs(alternate);
        alternate.LastCredential.Should().Be("alternate-secret");
        alternate.LastEndpoint.Should().Be("https://alternate.example");
        Assert.Throws<InvalidOperationException>(() =>
            factory.Create(scopedConfiguration, plugins, "unknown"));
    }

    private sealed class CredentialProvider(string providerKey)
        : IProviderPlugin, IProviderCredentialBoundPlugin, IProviderApiClient
    {
        public string ProviderKey => providerKey;
        public string DisplayName => providerKey;
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => true;
        public IModelCapabilityResolver Capabilities { get; } = new EmptyCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public string? LastCredential { get; private set; }
        public string? LastEndpoint { get; private set; }

        public IProviderApiClient CreateClient(ProviderClientOptions options) =>
            throw new InvalidOperationException("Credential binding is required.");

        public IProviderApiClient CreateClient(
            ProviderClientOptions options,
            string credential)
        {
            LastEndpoint = options.Endpoint;
            LastCredential = credential;
            return this;
        }

        public IProviderCostFeed? CreateCostFeed(
            ProviderClientOptions options,
            string credential) => null;

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
            Task.FromResult(new ChatCompletionResult { Content = "unused" });
    }

    private sealed class EmptyCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }
}
