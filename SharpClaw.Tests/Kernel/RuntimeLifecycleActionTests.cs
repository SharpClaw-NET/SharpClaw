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

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class RuntimeLifecycleActionTests
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
    public async Task Adapter_routes_the_complete_K01_lifecycle_through_one_action_pathAsync()
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
            }).ConfigureAwait(false);
        await adapter.StartAsync("test-host").ConfigureAwait(false);
        await adapter.RunRuntimeLifecycleActionAsync(
            Action("runtime.start.bind"),
            "loopback",
            _ =>
            {
                probe.Terminals.Enqueue("runtime.start.bind");
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        await adapter.StopAsync(
            onComplete: _ =>
            {
                probe.Terminals.Enqueue("runtime.stop.complete");
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        probe.Actions.Should().Equal(LifecycleActionNames);
        probe.Terminals.Should().Equal(
            "runtime.start.prepare",
            "runtime.start.bind",
            "runtime.stop.complete");
        probe.RegistrationStopCount.Should().Be(1);
    }

    [Test]
    public async Task Cancelled_K01_action_does_not_run_its_terminal_or_start_the_hostAsync()
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
            }).ConfigureAwait(false);

        await action.Should().ThrowAsync<KernelActionCancelledException>().ConfigureAwait(false);
        terminalCalls.Should().Be(0);
        probe.Actions.Should().ContainSingle().Which.Should().Be("runtime.start.prepare");
    }

    [TestCase(false), TestCase(true)]
    [Parallelizable]
    [CancelAfter(90_000)]
    public async Task StartupInitializationOutlivesShortPreparationWithoutRepeatingAsync(bool withModule)
    {
        var probe = new LifecycleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = withModule
            ? CreateAdapter(workspace, probe)
            : RuntimeKernelAdapterTestFactory.Create(new ConfigurationBuilder().Build(), [],
                workspace.CreateInstancePaths(), new LifecycleProviderClientFactory(new LifecycleProviderClient()));
        var calls = 0;
        var completed = false;

        await adapter.RunRuntimeLifecycleActionAsync(
            Action("runtime.start.prepare"),
            null,
            async cancellationToken =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(TimeSpan.FromSeconds(32), cancellationToken).ConfigureAwait(false);
                completed = true;
            }).ConfigureAwait(false);

        calls.Should().Be(1);
        completed.Should().BeTrue();
        if (withModule)
        {
            probe.Actions.Should().ContainSingle().Which.Should().Be("runtime.start.prepare");
            probe.InitializationContexts.Should().ContainSingle().Which.Action.Should().Be("runtime.start.prepare");
        }
        adapter.Graph.GetStandardAction(Action("runtime.start.prepare"))
            .DefaultTimeout.Should().Be(TimeSpan.FromSeconds(30));
        RuntimeStartupActionDefinitions.Initialize.DefaultTimeout.Should().Be(TimeSpan.FromMinutes(2));
        RuntimeStartupActionDefinitions.Initialize.RepeatPolicy.Kind.Should().Be(ActionRepeatKind.None);
    }

    [TestCase("cancel"), TestCase("fail"), TestCase("replace-input"), TestCase("replace-result"), TestCase("repeat")]
    public async Task InitializationModuleCannotSkipOrRepeatRequiredTerminalAsync(string operation)
    {
        var probe = new LifecycleProbe { InitializationOperation = operation };
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);
        var calls = 0;
        Func<Task> prepare = async () => await adapter.RunRuntimeLifecycleActionAsync(
            Action("runtime.start.prepare"), null, _ =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        if (string.Equals(operation, "cancel", StringComparison.Ordinal))
            await prepare.Should().ThrowAsync<KernelActionCancelledException>().ConfigureAwait(false);
        else
            await prepare.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
        calls.Should().Be(0);
        probe.InitializationContexts.Should().ContainSingle();
    }

    [TestCase("skip"), TestCase("swallow-failure")]
    public async Task InitializationCannotReportSuccessWithoutCompletedTerminalAsync(string operation)
    {
        var probe = new LifecycleProbe { InitializationOperation = operation };
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);
        var calls = 0;
        Func<Task> prepare = async () => await adapter.RunRuntimeLifecycleActionAsync(
            Action("runtime.start.prepare"), null, _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("Initialization failed before readiness.");
            }).ConfigureAwait(false);

        await prepare.Should().ThrowAsync<KernelActionFailedException>()
            .WithMessage("*this control did not issue*").ConfigureAwait(false);
        calls.Should().Be(string.Equals(operation, "skip", StringComparison.Ordinal) ? 0 : 1);
    }

    [Test]
    public async Task ReplacedPreparationSuccessDoesNotAuthorizeInitializationAsync()
    {
        var probe = new LifecycleProbe { SkipPreparation = true };
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);
        var calls = 0;
        Func<Task> prepare = async () => await adapter.RunRuntimeLifecycleActionAsync(
            Action("runtime.start.prepare"), null, _ =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        await prepare.Should().ThrowAsync<KernelActionExecutionException>().ConfigureAwait(false);
        calls.Should().Be(0);
        probe.InitializationContexts.Should().BeEmpty();
    }

    [TestCase(false), TestCase(true)]
    public async Task CallerCancellationPreventsInitializationSuccessAsync(bool duringTerminal)
    {
        var probe = new LifecycleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        if (!duringTerminal)
            await cancellation.CancelAsync().ConfigureAwait(false);
        var preparation = adapter.RunRuntimeLifecycleActionAsync(
            Action("runtime.start.prepare"), null, async ct =>
            {
                Interlocked.Increment(ref calls);
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }, cancellation.Token).AsTask();
        if (duringTerminal)
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        // The test owns this operation/signal; it runs without a JoinableTaskFactory dependency.
#pragma warning disable VSTHRD003
        Func<Task> observe = () => preparation;
#pragma warning restore VSTHRD003
        await observe.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
        calls.Should().Be(duringTerminal ? 1 : 0);
    }

    [Test]
    public async Task StartupPreparationAndInitializationShareHostIdentityButNotDeadlineAsync()
    {
        var probe = new LifecycleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);
        await adapter.RunRuntimeLifecycleActionAsync(Action("runtime.start.prepare"), null,
            _ => ValueTask.CompletedTask).ConfigureAwait(false);
        var preparation = probe.PreparationContexts.Should().ContainSingle().Which;
        var initialization = probe.InitializationContexts.Should().ContainSingle().Which;
        initialization.TraceId.Should().Be(preparation.TraceId);
        initialization.IdempotencyKey.Should().Be(preparation.IdempotencyKey);
        initialization.Caller.Should().Be(preparation.Caller);
        initialization.Features.Should().Be(preparation.Features);
        initialization.InvocationId.Should().NotBe(preparation.InvocationId);
        (initialization.Deadline - preparation.Deadline).Should().BeGreaterThan(TimeSpan.FromSeconds(85));
    }

    [Test]
    public Task StopPrepareCancellation_still_runs_host_cleanupAsync() =>
        AssertCleanupAfterStopInterceptionAsync(
            "runtime.stop.prepare",
            cancel: true);

    [Test]
    public Task StopPrepareFailure_still_runs_host_cleanupAsync() =>
        AssertCleanupAfterStopInterceptionAsync(
            "runtime.stop.prepare",
            cancel: false);

    [Test]
    public Task StopCompleteCancellation_still_runs_host_cleanupAsync() =>
        AssertCleanupAfterStopInterceptionAsync(
            "runtime.stop.complete",
            cancel: true);

    [Test]
    public Task StopCompleteFailure_still_runs_host_cleanupAsync() =>
        AssertCleanupAfterStopInterceptionAsync(
            "runtime.stop.complete",
            cancel: false);

    [Test]
    [NonParallelizable]
    public async Task Shutdown_stops_listener_before_registrations_and_rejects_new_requestsAsync()
    {
        var probe = new LifecycleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, probe);
        await adapter.StartAsync("test-host").ConfigureAwait(false);

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
        await app.StartAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);

        using var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Should().ContainSingle().Which),
        };
        var cleanup = new RuntimeHostCleanup(
            () => probe.ShutdownEvents.Enqueue("not-ready"),
            () => probe.ShutdownEvents.Enqueue("discovery"),
            () => probe.ShutdownEvents.Enqueue("api-key"),
            async () => await StopListenerAndProbeAdmissionAsync(app, client, probe).ConfigureAwait(false));

        try
        {
            await adapter.StopAsync(
                onPrepare: _ => cleanup.BeginAsync(),
                onComplete: _ => cleanup.CompleteAsync(), cancellationToken: CancellationToken.None).ConfigureAwait(false);

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
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task StopListenerAndProbeAdmissionAsync(WebApplication app, HttpClient client, LifecycleProbe probe)
    {
        probe.ShutdownEvents.Enqueue("listener");
        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await client.GetAsync(
                new Uri("/shutdown-probe", UriKind.RelativeOrAbsolute), timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // A stopped listener rejects admission at the transport boundary.
        }
        catch (TaskCanceledException)
        {
            // The existing bounded refusal probe can expire while the listener is closed.
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
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
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
        grants.Add(RuntimeStartupActionDefinitions.Initialize.Key.Value,
            RuntimeStartupActionDefinitions.Initialize.Capabilities);
        return RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [new LifecycleRegistration(provider, probe)],
            workspace.CreateInstancePaths(),
            new LifecycleProviderClientFactory(provider),
            new KernelGraphCompileOptions
            {
                ActionRegistrationCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
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
        await adapter.StartAsync("test-host").ConfigureAwait(false);

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
            onComplete: _ => cleanup.CompleteAsync()).ConfigureAwait(false);
        if (cancel)
            await stop.Should().ThrowAsync<KernelActionCancelledException>().ConfigureAwait(false);
        else
            await stop.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);

        cleanup.PreparationAttempted.Should().BeTrue();
        cleanup.CompletionAttempted.Should().BeTrue();
        cleanupEvents.Should().Equal("not-ready", "listener", "discovery", "api-key");
        probe.RegistrationStopCount.Should().Be(1);
        probe.Actions.Should().Contain(actionName);
    }

    private sealed class LifecycleProbe
    {
        public ConcurrentQueue<string> Actions { get; } = new();

        public ConcurrentQueue<ActionContext<KernelActionEnvelope>> PreparationContexts { get; } = new();

        public ConcurrentQueue<ActionContext<string>> InitializationContexts { get; } = new();

        public string InitializationOperation { get; init; } = "proceed";

        public bool SkipPreparation { get; init; }

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
            if (string.Equals(context.ActionKey.Value, "runtime.start.prepare", StringComparison.Ordinal))
            {
                probe.PreparationContexts.Enqueue(context);
                if (probe.SkipPreparation)
                    return ValueTask.FromResult(control.ReplaceResult(true, "Test skipped required preparation."));
            }
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

    private sealed class StartupInitializationInterceptor(LifecycleProbe probe) : IActionInterceptor<string, bool>
    {
        public async ValueTask<IActionOutcome<bool>> InvokeAsync(ActionContext<string> context,
            IActionControl<string, bool> control, CancellationToken cancellationToken)
        {
            probe.InitializationContexts.Enqueue(context);
            switch (probe.InitializationOperation)
            {
                case "cancel": return control.Cancel("TEST_CANCEL", "Initialization cancelled.");
                case "fail": return control.Fail(new ExecutionError("TEST_FAILURE", "Initialization denied."));
                case "replace-input":
                    return await control.ProceedWithInputAsync(
                    new ActionReplacement<string>("wrong-preparation", "Test invalid replacement."), cancellationToken).ConfigureAwait(false);
                case "replace-result": return control.ReplaceResult(true, "Test skipped required terminal.");
                case "repeat":
                    return await control.RepeatAsync(
                    new ActionRepeatRequest<string>(context.Action, "Test invalid repeat."), cancellationToken).ConfigureAwait(false);
                case "skip": return KernelActionOutcome<bool>.Completed(true);
                case "swallow-failure":
                    await control.ProceedAsync(cancellationToken).ConfigureAwait(false);
                    return KernelActionOutcome<bool>.Completed(true);
                default: return await control.ProceedAsync(cancellationToken).ConfigureAwait(false);
            }
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
            module.AddSingleton(new StartupInitializationInterceptor(probe));
            module.OnAction(RuntimeStartupActionDefinitions.Initialize)
                .Use<StartupInitializationInterceptor>(new HookOrdering(
                    "startup-initialization", HookPriority.Normal, [], [], TimeSpan.FromMinutes(2), HookFailurePolicy.FailAction));
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
            IReadOnlyList<IProviderPlugin> plugins,
            string providerKey) => client;
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
            catch (IOException exception)
            {
                TestContext.Progress.WriteLine($"Temporary directory cleanup failed: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                TestContext.Progress.WriteLine($"Temporary directory cleanup failed: {exception.Message}");
            }
        }
    }
}
