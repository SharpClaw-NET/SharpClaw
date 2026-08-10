using System.Collections.Concurrent;
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
public sealed class RuntimeLifecycleActionTests
{
    private static readonly string[] LifecycleActionNames =
    [
        "runtime.start.prepare",
        "runtime.start.configure",
        "runtime.start.bind",
        "runtime.stop.prepare",
        "runtime.stop.complete",
    ];

    [Test]
    public async Task Adapter_routes_the_complete_K01_lifecycle_through_one_action_path()
    {
        var probe = new LifecycleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);

        await adapter.RunRuntimeLifecycleActionAsync(
            Action("runtime.start.prepare"),
            null,
            _ =>
            {
                probe.Terminals.Enqueue("runtime.start.prepare");
                return ValueTask.CompletedTask;
            });
        await adapter.StartAsync("test-host");
        await adapter.RunRuntimeLifecycleActionAsync(
            Action("runtime.start.bind"),
            "loopback",
            _ =>
            {
                probe.Terminals.Enqueue("runtime.start.bind");
                return ValueTask.CompletedTask;
            });

        await adapter.StopAsync(
            onComplete: _ =>
            {
                probe.Terminals.Enqueue("runtime.stop.complete");
                return ValueTask.CompletedTask;
            });

        probe.Actions.Should().Equal(LifecycleActionNames);
        probe.Terminals.Should().Equal(
            "runtime.start.prepare",
            "runtime.start.bind",
            "runtime.stop.complete");
    }

    [Test]
    public async Task Cancelled_K01_action_does_not_run_its_terminal_or_start_the_host()
    {
        var probe = new LifecycleProbe { Cancel = true };
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);
        var terminalCalls = 0;

        Func<Task> action = async () => await adapter.RunRuntimeLifecycleActionAsync(
            Action("runtime.start.prepare"),
            null,
            _ =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.CompletedTask;
            });

        await action.Should().ThrowAsync<KernelActionCancelledException>();
        terminalCalls.Should().Be(0);
        probe.Actions.Should().ContainSingle().Which.Should().Be("runtime.start.prepare");
    }

    [Test]
    public void Production_source_maps_each_K01_action_to_the_runtime_boundary()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        root.Should().NotBeNullOrWhiteSpace();

        var adapterSource = File.ReadAllText(Path.Combine(
            root!,
            "SharpClaw.Runtime",
            "BLL",
            "Kernel",
            "RuntimeKernelAdapter.cs"));
        var hostSource = File.ReadAllText(Path.Combine(
            root!,
            "SharpClaw.Runtime",
            "Host",
            "LocalRuntimeHost.cs"));

        hostSource.Should().Contain("RuntimeLifecycleActionCatalog.StartPrepare");
        hostSource.Should().Contain("RuntimeLifecycleActionCatalog.StartBind");
        adapterSource.Should().Contain("RuntimeLifecycleActionCatalog.StartConfigure");
        adapterSource.Should().Contain("RuntimeLifecycleActionCatalog.StopPrepare");
        adapterSource.Should().Contain("RuntimeLifecycleActionCatalog.StopComplete");
        LifecycleActionNames.Should().OnlyContain(name =>
            SharpClawActionCatalog.Kernel.Any(action => action.Value == name));
    }

    private static RuntimeKernelAdapter CreateAdapter(
        TemporaryWorkspace workspace,
        LifecycleProbe probe)
    {
        var provider = new LifecycleProviderClient();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "lifecycle-test",
                ["Provider:Model"] = "lifecycle-model",
            })
            .Build();
        var moduleId = "k01-lifecycle-test";
        var grants = LifecycleActionNames.ToDictionary(
            name => name,
            name => KernelActionCatalog.DescriptorFor(Action(name)).Capabilities,
            StringComparer.Ordinal);
        return new RuntimeKernelAdapter(
            configuration,
            new ServiceCollection().BuildServiceProvider(),
            new InMemoryConversationStore(),
            [new LifecycleModule(provider, probe)],
            workspace.CreateInstancePaths(),
            new LifecycleProviderClientFactory(provider),
            new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    [moduleId] = grants,
                },
            });
    }

    private static SharpClawActionKey Action(string value) => new(value);

    private sealed class LifecycleProbe
    {
        public ConcurrentQueue<string> Actions { get; } = new();

        public ConcurrentQueue<string> Terminals { get; } = new();

        public bool Cancel { get; init; }

        public void Record(string actionKey) => Actions.Enqueue(actionKey);
    }

    private sealed class LifecycleInterceptor(LifecycleProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.Record(context.ActionKey.Value);
            return probe.Cancel
                ? ValueTask.FromResult(control.Cancel(
                    "K01_TEST_CANCELLED",
                    "The K01 lifecycle test cancelled this action."))
                : control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class LifecycleModule(
        IProviderPlugin provider,
        LifecycleProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("k01-lifecycle-test", "K01 lifecycle test", "k01");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<IProviderPlugin>(provider);
            module.Services.AddSingleton(probe);
            module.Services.AddSingleton<LifecycleInterceptor>();
            foreach (var actionName in LifecycleActionNames)
            {
                module.Hooks
                    .For(Action(actionName))
                    .Use<LifecycleInterceptor>(new HookOrdering(
                        $"k01-lifecycle-{actionName}",
                        HookPriority.Normal,
                        [],
                        [],
                        TimeSpan.FromSeconds(5),
                        HookFailurePolicy.FailAction));
            }
        }

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class LifecycleProviderClientFactory(IProviderApiClient client)
        : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins) => client;
    }

    private sealed class LifecycleProviderClient : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => "lifecycle-test";

        public string DisplayName => "K01 lifecycle provider";

        public bool RequiresEndpoint => false;

        public bool RequiresApiKey => false;

        public IModelCapabilityResolver Capabilities { get; } =
            new EmptyCapabilityResolver();

        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];

        public IDeviceCodeFlow? DeviceCodeFlow => null;

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["lifecycle-model"]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ChatCompletionResult
            {
                Content = "lifecycle-response",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
    }

    private sealed class EmptyCapabilityResolver : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-k01-" + Guid.NewGuid().ToString("N"));

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
}
