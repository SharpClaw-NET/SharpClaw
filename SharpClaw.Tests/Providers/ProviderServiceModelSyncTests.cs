using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Tests.Providers;

[TestFixture]
public sealed class ProviderServiceModelSyncTests
{
    [Test]
    public async Task SyncModelsAsync_UsesCanonicalModelListForPackageBuiltProviderClient()
    {
        var client = new CanonicalOnlyProviderClient();
        await using var services = CreateServices(new CanonicalOnlyProviderPlugin(client));
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var provider = new ProviderDB
        {
            Id = Guid.NewGuid(),
            Name = "ElevenLabs",
            ProviderKey = CanonicalOnlyProviderPlugin.Key,
            EncryptedApiKey = "stored-provider-key",
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<ProviderService>();

        var models = await service.SyncModelsAsync(provider.Id);

        client.CanonicalModelListCallCount.Should().Be(1);
        models.Select(model => model.Name).Should().ContainSingle("eleven_multilingual_v2");
        db.Models.Single().ProviderId.Should().Be(provider.Id);
    }

    private static ServiceProvider CreateServices(IProviderPlugin plugin)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var databaseRoot = new InMemoryDatabaseRoot();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(new EncryptionOptions
        {
            Key = new byte[32],
            EncryptProviderKeys = false,
        });
        services.AddSingleton(plugin);
        services.AddSingleton<ProviderApiClientFactory>();
        services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(
                "ProviderServiceModelSync_" + Guid.NewGuid().ToString("N"),
                databaseRoot));
        services.AddScoped<ProviderService>();

        return services.BuildServiceProvider();
    }

    private sealed class CanonicalOnlyProviderPlugin(
        CanonicalOnlyProviderClient client) : IProviderPlugin
    {
        public const string Key = "elevenlabs";

        public string ProviderKey => Key;
        public string DisplayName => "ElevenLabs";
        public string OwnerModuleId => "curativa";
        public bool RequiresEndpoint => false;
        public bool SupportsAutomaticEndpointDiscovery => false;
        public bool IsSeedable => false;
        public bool RequiresApiKey => true;
        public IModelCapabilityResolver Capabilities { get; } = new CanonicalOnlyCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public ICompletionParameterSpec ParameterSpec => ICompletionParameterSpec.Passthrough;
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public bool SupportsCostFeed => false;
        public string CostFeedPermissionDeniedNote => string.Empty;

        public IProviderApiClient CreateClient(ProviderClientOptions options) => client;

        public IProviderCostFeed? CreateCostFeed(ProviderClientOptions options) => null;
    }

    private sealed class CanonicalOnlyProviderClient : IProviderApiClient
    {
        public int CanonicalModelListCallCount { get; private set; }
        public string ProviderKey => CanonicalOnlyProviderPlugin.Key;
        public bool SupportsNativeToolCalling => false;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default)
        {
            CanonicalModelListCallCount++;
            return Task.FromResult<IReadOnlyList<string>>(["eleven_multilingual_v2"]);
        }

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The model-sync regression test only exercises model discovery.");
    }

    private sealed class CanonicalOnlyCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => ["speech-to-text"];
    }
}
