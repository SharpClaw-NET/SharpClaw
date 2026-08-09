using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeKernelAdapterTests
{
    [Test]
    public async Task Adapter_compiles_module_graph_and_routes_direct_chat_through_module_provider()
    {
        var provider = new RecordingProviderClient();
        var module = new ProviderModule(provider);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();
        using var hostServices = new ServiceCollection().BuildServiceProvider();
        using var workspace = new TemporaryWorkspace();
        var instancePaths = workspace.CreateInstancePaths();
        var providerFactory = new RecordingProviderClientFactory(provider);

        var adapter = new RuntimeKernelAdapter(
            configuration,
            hostServices,
            new InMemoryConversationStore(),
            [module],
            instancePaths,
            providerFactory);

        adapter.Graph.Modules.Modules.Should().ContainSingle()
            .Which.Identity.Id.Should().Be("test-module");
        adapter.Graph.GetService(typeof(IEnumerable<IProviderPlugin>))
            .Should().NotBeNull();
        providerFactory.Plugins.Should().ContainSingle()
            .Which.ProviderKey.Should().Be("test");

        await adapter.StartAsync("test-host");
        var result = await adapter.Kernel.RunAsync(new ChatTurnInput("hello"));
        await adapter.StopAsync();

        result.Completion.Content.Should().Be("reply");
        provider.Messages.Should().ContainSingle(message => message.Content == "hello");
        module.Started.Should().BeTrue();
        module.Stopped.Should().BeTrue();
    }

    [Test]
    public async Task Adapter_uses_the_instance_manifest_for_restart_stable_direct_chat_history()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();
        using var workspace = new TemporaryWorkspace();
        var instancePaths = workspace.CreateInstancePaths();
        var firstStore = new InMemoryConversationStore();
        var firstModule = new ProviderModule(new RecordingProviderClient());
        var firstAdapter = new RuntimeKernelAdapter(
            configuration,
            new ServiceCollection().BuildServiceProvider(),
            firstStore,
            [firstModule],
            instancePaths,
            new RecordingProviderClientFactory(firstModule.Provider));

        await firstAdapter.StartAsync("test-host");
        var firstResult = await firstAdapter.Kernel.RunAsync(new ChatTurnInput("first"));
        await firstAdapter.StopAsync();

        var secondModule = new ProviderModule(new RecordingProviderClient());
        var secondAdapter = new RuntimeKernelAdapter(
            configuration,
            new ServiceCollection().BuildServiceProvider(),
            firstStore,
            [secondModule],
            new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                instancePaths.InstanceRoot,
                instancePaths.SharedRoot,
                instancePaths.InstallAnchor),
            new RecordingProviderClientFactory(secondModule.Provider));

        await secondAdapter.StartAsync("test-host");
        var secondResult = await secondAdapter.Kernel.RunAsync(new ChatTurnInput("second"));
        await secondAdapter.StopAsync();

        secondResult.ConversationId.Should().Be(firstResult.ConversationId);
        (await firstStore.LoadHistoryAsync(secondResult.ConversationId, CancellationToken.None))
            .Should().HaveCount(4);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-kernel-" + Guid.NewGuid().ToString("N"));

        public SharpClawInstancePaths CreateInstancePaths()
        {
            Directory.CreateDirectory(_root);
            var paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                _root,
                _root,
                _root);
            paths.EnsureDirectories();
            return paths;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class ProviderModule(IProviderPlugin provider) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("test-module", "Test module", "test");

        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public IProviderApiClient Provider => (IProviderApiClient)provider;

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Services.AddSingleton<IProviderPlugin>(provider);

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct)
        {
            Started = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken ct)
        {
            Stopped = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingProviderClientFactory(IProviderApiClient client)
        : IRuntimeProviderClientFactory
    {
        public IReadOnlyList<IProviderPlugin>? Plugins { get; private set; }

        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins)
        {
            Plugins = plugins;
            return client;
        }
    }

    private sealed class RecordingProviderClient : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => "test";
        public string DisplayName => "Test";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public IModelCapabilityResolver Capabilities { get; } =
            new EmptyCapabilityResolver();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public List<ChatCompletionMessage> Messages { get; } = [];

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default)
        {
            Messages.AddRange(messages);
            return Task.FromResult(new ChatCompletionResult
            {
                Content = "reply",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
        }
    }

    private sealed class EmptyCapabilityResolver : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }
}
