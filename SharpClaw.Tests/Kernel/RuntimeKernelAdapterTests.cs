using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
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

        adapter.Graph.Modules.Modules.Should().HaveCount(2);
        adapter.Graph.Modules.Modules.Select(module => module.Identity.Id)
            .Should()
            .BeEquivalentTo(["sharpclaw.runtime.events", "test-module"]);
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
    public async Task Adapter_dispatches_request_ingress_through_the_compiled_action_graph()
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
        using var workspace = new TemporaryWorkspace();
        var adapter = new RuntimeKernelAdapter(
            configuration,
            new ServiceCollection().BuildServiceProvider(),
            new InMemoryConversationStore(),
            [module],
            workspace.CreateInstancePaths(),
            new RecordingProviderClientFactory(provider));

        adapter.Graph.ContainsAction(new SharpClawActionKey("runtime.request.receive"))
            .Should().BeTrue();
        var executionContext = new KernelActionExecutionContext(
            new RequestPrincipal(
                "request-user",
                "Request user",
                new HashSet<string>(StringComparer.Ordinal),
                true),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());
        var result = await adapter.RunRequestAsync(
            executionContext,
            "request-payload",
            static (payload, _) => ValueTask.FromResult(payload.Length));

        result.Should().Be("request-payload".Length);
    }

    [Test]
    public async Task Request_stream_replace_result_without_terminal_fails_closed()
    {
        var provider = new RecordingProviderClient();
        var module = new StreamReplacementModule(provider);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();
        using var workspace = new TemporaryWorkspace();
        var conversationStore = new InMemoryConversationStore();
        var actionKey = new SharpClawActionKey("runtime.request.receive");
        var descriptor = KernelActionCatalog.DescriptorFor(actionKey).ToDescriptor();
        var types = KernelSchemaIdentity.ActionTypes(
            descriptor,
            typeof(KernelActionEnvelope),
            typeof(object));
        var adapter = new RuntimeKernelAdapter(
            configuration,
            new ServiceCollection().BuildServiceProvider(),
            conversationStore,
            [module],
            workspace.CreateInstancePaths(),
            new RecordingProviderClientFactory(provider),
            new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    [module.Identity.Id] = new Dictionary<
                        string,
                        ActionInterceptionCapabilities>(StringComparer.Ordinal)
                    {
                        [actionKey.Value] = descriptor.Capabilities,
                    },
                },
                SensitiveActionApprovals =
                [
                    new KernelSensitiveActionApproval(
                        module.Identity.Id,
                        actionKey,
                        descriptor.Version,
                        types.ActionType.AssemblyQualifiedName!,
                        types.ResultType.AssemblyQualifiedName!,
                        KernelSchemaIdentity.Action(
                            descriptor,
                            typeof(KernelActionEnvelope),
                            typeof(object))),
                ],
            });
        var conversationId = Guid.NewGuid();
        var executionContext = new KernelActionExecutionContext(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());
        var chunks = new List<ChatStreamChunk>();

        Func<Task> consume = async () =>
        {
            await foreach (var chunk in adapter.RunRequestStreamAsync(
                               executionContext,
                               "stream-request",
                               (_, ct) => adapter.Kernel.StreamAsync(
                                   new ChatTurnInput("must-not-run", conversationId),
                                   ct)))
            {
                chunks.Add(chunk);
            }
        };

        await consume.Should()
            .ThrowAsync<KernelActionExecutionException>()
            .WithMessage("*without running its terminal*");

        chunks.Should().BeEmpty();
        provider.Messages.Should().BeEmpty();
        (await conversationStore.LoadHistoryAsync(
            conversationId,
            CancellationToken.None)).Should().BeEmpty();
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

    private sealed class StreamReplacementModule(IProviderPlugin provider) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("stream-replacement-module", "Stream replacement module", "stream-replace");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<IProviderPlugin>(provider);
            module.Services.AddSingleton<StreamReplacementInterceptor>();
            module.Hooks.For(new SharpClawActionKey("runtime.request.receive"))
                .Use<StreamReplacementInterceptor>(new HookOrdering(
                    "stream-replacement-test",
                    HookPriority.Normal,
                    [],
                    [],
                    TimeSpan.FromSeconds(5),
                    HookFailurePolicy.FailAction));
        }

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class StreamReplacementInterceptor
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(control.ReplaceResult(
                true,
                "K06 stream replacement test"));
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
