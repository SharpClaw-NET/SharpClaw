using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
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
        probe.RegistrationStopCount.Should().Be(1);
    }

    [Test]
    public async Task Cancelled_K01_action_does_not_run_its_terminal_or_start_the_host()
    {
        var probe = new LifecycleProbe { CancelAction = "runtime.start.prepare" };
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
    public Task StopPrepareCancellation_still_runs_host_cleanup() =>
        AssertCleanupAfterStopInterceptionAsync(
            "runtime.stop.prepare",
            cancel: true);

    [Test]
    public Task StopPrepareFailure_still_runs_host_cleanup() =>
        AssertCleanupAfterStopInterceptionAsync(
            "runtime.stop.prepare",
            cancel: false);

    [Test]
    public Task StopCompleteCancellation_still_runs_host_cleanup() =>
        AssertCleanupAfterStopInterceptionAsync(
            "runtime.stop.complete",
            cancel: true);

    [Test]
    public Task StopCompleteFailure_still_runs_host_cleanup() =>
        AssertCleanupAfterStopInterceptionAsync(
            "runtime.stop.complete",
            cancel: false);

    [Test]
    [NonParallelizable]
    public async Task Shutdown_stops_listener_before_registrations_and_rejects_new_requests()
    {
        var probe = new LifecycleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);
        await adapter.StartAsync("test-host");

        var requestCount = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet(
            "/shutdown-probe",
            () =>
            {
                Interlocked.Increment(ref requestCount);
                return Results.Ok();
            });
        await app.StartAsync();

        using var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single()),
        };
        var cleanup = new RuntimeHostCleanup(
            () => probe.ShutdownEvents.Enqueue("not-ready"),
            () => probe.ShutdownEvents.Enqueue("discovery"),
            () => probe.ShutdownEvents.Enqueue("api-key"),
            async () =>
            {
                probe.ShutdownEvents.Enqueue("listener");
                await app.StopAsync(CancellationToken.None);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await client.GetAsync("/shutdown-probe", timeout.Token);
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException)
                {
                }
            });

        try
        {
            await adapter.StopAsync(
                onPrepare: _ => cleanup.BeginAsync(),
                onComplete: _ => cleanup.CompleteAsync());

            requestCount.Should().Be(0);
            probe.RegistrationStopCount.Should().Be(1);
            probe.ShutdownEvents.Should().Equal(
                "not-ready",
                "listener",
                "module-stop",
                "discovery",
                "api-key");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Test]
    public void Production_source_maps_each_K01_action_to_the_runtime_boundary()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? FindSourceRoot();

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
        var cleanupSource = File.ReadAllText(Path.Combine(
            root!,
            "SharpClaw.Runtime",
            "BLL",
            "Kernel",
            "RuntimeHostCleanup.cs"));

        hostSource.Should().Contain("RuntimeLifecycleActionCatalog.StartPrepare");
        hostSource.Should().Contain("RuntimeLifecycleActionCatalog.StartBind");
        adapterSource.Should().Contain("RuntimeLifecycleActionCatalog.StartConfigure");
        adapterSource.Should().Contain("RuntimeLifecycleActionCatalog.StopPrepare");
        adapterSource.Should().Contain("RuntimeLifecycleActionCatalog.StopComplete");
        hostSource.Should().Contain("new RuntimeHostCleanup(");
        hostSource.Should().Contain("_ => cleanup.BeginAsync()");
        hostSource.Should().Contain("_ => cleanup.CompleteAsync()");
        hostSource.Should().Contain("if (!cleanup.PreparationAttempted)");
        hostSource.Should().Contain("if (!cleanup.CompletionAttempted)");
        cleanupSource.Should().Contain("Interlocked.Exchange(ref _preparationAttempted, 1)");
        cleanupSource.Should().Contain("Interlocked.Exchange(ref _completionAttempted, 1)");
        LifecycleActionNames.Should().OnlyContain(name =>
            SharpClawActionCatalog.Kernel.Any(action => action.Value == name));
    }

    private static string FindSourceRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot) &&
            Directory.Exists(Path.Combine(configuredRoot, "SharpClaw.Runtime")))
            return configuredRoot;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "SharpClaw.Runtime")))
                return directory.FullName;
        }

        throw new AssertionException("The SharpClaw source root could not be located.");
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
        var SourceId = "k01-lifecycle-test";
        var grants = LifecycleActionNames.ToDictionary(
            name => name,
            name => KernelActionCatalog.DescriptorFor(Action(name)).Capabilities,
            StringComparer.Ordinal);
        return RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [new LifecycleRegistration(provider, probe)],
            workspace.CreateInstancePaths(),
            new LifecycleProviderClientFactory(provider),
            new KernelGraphCompileOptions
            {
                ActionRegistrationCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    [SourceId] = grants,
                },
            });
    }

    private static SharpClawActionKey Action(string value) => new(value);

    private static async Task AssertCleanupAfterStopInterceptionAsync(
        string actionName,
        bool cancel)
    {
        var probe = new LifecycleProbe
        {
            CancelAction = cancel ? actionName : null,
            FailureAction = cancel ? null : actionName,
        };
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);
        await adapter.StartAsync("test-host");

        var cleanupEvents = new ConcurrentQueue<string>();
        var cleanup = new RuntimeHostCleanup(
            () => cleanupEvents.Enqueue("not-ready"),
            () => cleanupEvents.Enqueue("discovery"),
            () => cleanupEvents.Enqueue("api-key"),
            () =>
            {
                cleanupEvents.Enqueue("listener");
                return ValueTask.CompletedTask;
            });

        Func<Task> stop = async () => await adapter.StopAsync(
            onPrepare: _ => cleanup.BeginAsync(),
            onComplete: _ => cleanup.CompleteAsync());
        if (cancel)
            await stop.Should().ThrowAsync<KernelActionCancelledException>();
        else
            await stop.Should().ThrowAsync<KernelActionFailedException>();

        cleanup.PreparationAttempted.Should().BeTrue();
        cleanup.CompletionAttempted.Should().BeTrue();
        cleanupEvents.Should().Equal("not-ready", "listener", "discovery", "api-key");
        probe.RegistrationStopCount.Should().Be(1);
        probe.Actions.Should().Contain(actionName);
    }

    private sealed class LifecycleProbe
    {
        public ConcurrentQueue<string> Actions { get; } = new();

        public ConcurrentQueue<string> Terminals { get; } = new();

        public ConcurrentQueue<string> ShutdownEvents { get; } = new();

        private int _registrationStopCount;

        public int RegistrationStopCount => Volatile.Read(ref _registrationStopCount);

        public string? CancelAction { get; init; }

        public string? FailureAction { get; init; }

        public void Record(string actionKey) => Actions.Enqueue(actionKey);

        public void RecordRegistrationStop()
        {
            Interlocked.Increment(ref _registrationStopCount);
            ShutdownEvents.Enqueue("module-stop");
        }

        public bool ShouldCancel(string actionKey) =>
            string.Equals(CancelAction, actionKey, StringComparison.Ordinal);

        public bool ShouldFail(string actionKey) =>
            string.Equals(FailureAction, actionKey, StringComparison.Ordinal);
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
            if (probe.ShouldCancel(context.ActionKey.Value))
            {
                return ValueTask.FromResult(control.Cancel(
                    "K01_TEST_CANCELLED",
                    "The K01 lifecycle test cancelled this action."));
            }

            if (probe.ShouldFail(context.ActionKey.Value))
            {
                return ValueTask.FromResult(control.Fail(new ExecutionError(
                    "K01_TEST_FAILED",
                    "The K01 lifecycle test failed this action.")));
            }

            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class LifecycleRegistration(
        IProviderPlugin provider,
        LifecycleProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("k01-lifecycle-test", "K01 lifecycle test", "k01");

        public void ConfigureServices(IServiceCollection module)
        {
            module.AddSingleton<IProviderPlugin>(provider);
            module.AddSingleton(probe);
            module.AddSingleton<LifecycleInterceptor>();
            foreach (var actionName in LifecycleActionNames)
            {
                module.OnAction(Action(actionName))
                    .Use<LifecycleInterceptor>(new HookOrdering(
                        $"k01-lifecycle-{actionName}",
                        HookPriority.Normal,
                        [],
                        [],
                        TimeSpan.FromSeconds(5),
                        HookFailurePolicy.FailAction));
            }
        }

        public ValueTask StartAsync(ServiceStartContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            probe.RecordRegistrationStop();
            return ValueTask.CompletedTask;
        }
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
