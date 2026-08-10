using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
[NonParallelizable]
public sealed class RuntimeCliBoundaryTests
{
    private static readonly string[] ExpectedActions =
    [
        "runtime.cli.parse",
        "runtime.cli.command.select",
        "runtime.cli.execute",
        "runtime.cli.output.write",
        "runtime.cli.complete",
        "runtime.cli.fail",
        "runtime.cli.cancel",
    ];

    [Test]
    public void Runtime_cli_catalog_matches_the_published_kernel_catalog()
    {
        RuntimeCliActionCatalog.All
            .Select(static action => action.Value)
            .Should()
            .Equal(ExpectedActions);

        RuntimeCliActionCatalog.All
            .Should()
            .OnlyContain(action => SharpClawActionCatalog.Kernel.Contains(action));
    }

    [Test]
    public async Task Local_cli_help_runs_parse_select_execute_output_and_complete_once()
    {
        using var workspace = new TemporaryWorkspace();
        using var hostServices = new ServiceCollection().BuildServiceProvider();
        var probe = new CliProbe();
        var adapter = CreateAdapter(workspace, hostServices, probe);

        await adapter.StartAsync("k04-test");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await RuntimeCliSession.RunAsync(
                ["--cli", "help"],
                adapter,
                adapter.Kernel,
                output,
                error,
                CancellationToken.None);

            exitCode.Should().Be(0);
            output.ToString().Should().Contain("--cli chat");
            error.ToString().Should().BeEmpty();
            probe.Actions().Should().Equal(ExpectedActions.Take(5));
            probe.ShouldUseOneRootContext();
        }
        finally
        {
            await adapter.StopAsync();
        }
    }

    [Test]
    public async Task Local_cli_unknown_command_runs_failure_output_and_completion()
    {
        using var workspace = new TemporaryWorkspace();
        using var hostServices = new ServiceCollection().BuildServiceProvider();
        var probe = new CliProbe();
        var adapter = CreateAdapter(workspace, hostServices, probe);

        await adapter.StartAsync("k04-test");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await RuntimeCliSession.RunAsync(
                ["--cli", "unknown"],
                adapter,
                adapter.Kernel,
                output,
                error,
                CancellationToken.None);

            exitCode.Should().Be(1);
            output.ToString().Should().BeEmpty();
            error.ToString().Should().Contain("Unknown Runtime CLI command");
            probe.Actions().Should().Equal(
                "runtime.cli.parse",
                "runtime.cli.command.select",
                "runtime.cli.execute",
                "runtime.cli.fail",
                "runtime.cli.output.write",
                "runtime.cli.complete");
            probe.ShouldUseOneRootContext();
        }
        finally
        {
            await adapter.StopAsync();
        }
    }

    [Test]
    public async Task Local_cli_parse_failure_runs_failure_output_without_exposing_exception_text()
    {
        using var workspace = new TemporaryWorkspace();
        using var hostServices = new ServiceCollection().BuildServiceProvider();
        var probe = new CliProbe();
        var adapter = CreateAdapter(workspace, hostServices, probe);

        await adapter.StartAsync("k04-test");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await RuntimeCliSession.RunAsync(
                ["--cli"],
                adapter,
                adapter.Kernel,
                output,
                error,
                CancellationToken.None);

            exitCode.Should().Be(1);
            error.ToString().Should().Be("The Runtime CLI command failed." + Environment.NewLine);
            error.ToString().Should().NotContain("was not supplied");
            probe.Actions().Should().Equal(
                "runtime.cli.parse",
                "runtime.cli.fail",
                "runtime.cli.output.write");
        }
        finally
        {
            await adapter.StopAsync();
        }
    }

    [Test]
    public async Task Local_cli_cancellation_runs_cancel_and_output_with_the_same_context()
    {
        using var workspace = new TemporaryWorkspace();
        using var hostServices = new ServiceCollection().BuildServiceProvider();
        var probe = new CliProbe();
        var adapter = CreateAdapter(workspace, hostServices, probe);

        await adapter.StartAsync("k04-test");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var exitCode = await RuntimeCliSession.RunAsync(
                ["--cli", "help"],
                adapter,
                adapter.Kernel,
                output,
                error,
                cancellation.Token);

            exitCode.Should().Be(130);
            error.ToString().Should().Be("The Runtime CLI command was cancelled." + Environment.NewLine);
            probe.Actions().Should().ContainInOrder(
                "runtime.cli.cancel",
                "runtime.cli.output.write");
            probe.ShouldUseOneRootContext();
        }
        finally
        {
            await adapter.StopAsync();
        }
    }

    [Test]
    public async Task Local_cli_typed_action_cancellation_runs_cancel_and_output_without_failure()
    {
        using var workspace = new TemporaryWorkspace();
        using var hostServices = new ServiceCollection().BuildServiceProvider();
        var probe = new CliProbe
        {
            CancelAction = RuntimeCliActionCatalog.Execute.Value,
        };
        var adapter = CreateAdapter(workspace, hostServices, probe);

        await adapter.StartAsync("k04-test");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await RuntimeCliSession.RunAsync(
                ["--cli", "help"],
                adapter,
                adapter.Kernel,
                output,
                error,
                CancellationToken.None);

            exitCode.Should().Be(130);
            error.ToString().Should().Be(
                "The Runtime CLI command was cancelled." + Environment.NewLine);
            probe.Actions().Should().Equal(
                "runtime.cli.parse",
                "runtime.cli.command.select",
                "runtime.cli.execute",
                "runtime.cli.cancel",
                "runtime.cli.output.write");
            probe.Actions().Should().NotContain("runtime.cli.fail");
            probe.Actions().Should().NotContain("runtime.cli.complete");
        }
        finally
        {
            await adapter.StopAsync();
        }
    }

    [Test]
    public async Task Local_cli_action_cancellation_reaches_in_flight_chat_without_caller_cancellation()
    {
        using var workspace = new TemporaryWorkspace();
        using var hostServices = new ServiceCollection().BuildServiceProvider();
        using var actionCancellation = new CancellationTokenSource();
        var probe = new CliProbe
        {
            BlockChatUntilCancellation = true,
            ExecuteCancellationSource = actionCancellation,
        };
        var adapter = CreateAdapter(workspace, hostServices, probe);

        await adapter.StartAsync("k04-test");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var session = RuntimeCliSession.RunAsync(
                ["--cli", "chat", "cancel me"],
                adapter,
                adapter.Kernel,
                output,
                error,
                CancellationToken.None).AsTask();

            await probe.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            actionCancellation.Cancel();

            var exitCode = await session.WaitAsync(TimeSpan.FromSeconds(5));
            await probe.ChatCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            exitCode.Should().Be(130);
            error.ToString().Should().Be(
                "The Runtime CLI command was cancelled." + Environment.NewLine);
            probe.Actions().Should().ContainInOrder(
                "runtime.cli.parse",
                "runtime.cli.command.select",
                "runtime.cli.execute",
                "runtime.cli.cancel",
                "runtime.cli.output.write");
            probe.Actions().Should().NotContain("runtime.cli.fail");
            probe.Actions().Should().NotContain("runtime.cli.complete");
        }
        finally
        {
            await adapter.StopAsync();
        }
    }

    [Test]
    public async Task Concurrent_cli_sessions_use_distinct_root_contexts_without_a_second_dispatcher()
    {
        using var workspace = new TemporaryWorkspace();
        using var hostServices = new ServiceCollection().BuildServiceProvider();
        var probe = new CliProbe();
        var adapter = CreateAdapter(workspace, hostServices, probe);

        await adapter.StartAsync("k04-test");
        try
        {
            static async Task<int> RunHelpAsync(
                RuntimeKernelAdapter runtimeKernel,
                StringWriter output,
                StringWriter error) =>
                await RuntimeCliSession.RunAsync(
                    ["--cli", "help"],
                    runtimeKernel,
                    runtimeKernel.Kernel,
                    output,
                    error,
                    CancellationToken.None);

            using var firstOutput = new StringWriter();
            using var firstError = new StringWriter();
            using var secondOutput = new StringWriter();
            using var secondError = new StringWriter();
            var results = await Task.WhenAll(
                RunHelpAsync(adapter, firstOutput, firstError),
                RunHelpAsync(adapter, secondOutput, secondError));

            results.Should().Equal(0, 0);
            probe.Observations.Should().HaveCount(10);
            probe.Observations
                .Select(static observation => observation.TraceId)
                .Distinct()
                .Should()
                .HaveCount(2);
            probe.Observations
                .Select(static observation => observation.IdempotencyKey)
                .Distinct()
                .Should()
                .HaveCount(2);
            var successfulActions = ExpectedActions.Take(5).ToArray();
            probe.Observations
                .GroupBy(static observation => observation.TraceId)
                .Should()
                .OnlyContain(group => group.Select(static observation => observation.Action).OrderBy(
                    static action => Array.IndexOf(ExpectedActions, action)).SequenceEqual(successfulActions));
        }
        finally
        {
            await adapter.StopAsync();
        }
    }

    [Test]
    public void Live_cli_composition_uses_the_kernel_after_start_and_keeps_legacy_cli_excluded()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? FindSourceRoot();
        var hostSource = File.ReadAllText(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "Host",
            "LocalRuntimeHost.cs"));
        var launcherSource = File.ReadAllText(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "Host",
            "RuntimeLauncher.cs"));
        var hostProject = File.ReadAllText(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "Host",
            "SharpClaw.Runtime.Host.csproj"));
        var adapterSource = File.ReadAllText(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "BLL",
            "Kernel",
            "RuntimeKernelAdapter.cs"));
        var sessionSource = File.ReadAllText(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "Host",
            "RuntimeCliSession.cs"));
        var programSource = File.ReadAllText(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "Host",
            "Program.cs"));

        var kernelStart = hostSource.IndexOf(
            "await kernel.StartAsync",
            StringComparison.Ordinal);
        var cliBranch = hostSource.IndexOf(
            "RuntimeCliCommandLine.IsRequested",
            StringComparison.Ordinal);
        var listenerStart = hostSource.IndexOf(
            "await app.StartAsync",
            StringComparison.Ordinal);

        kernelStart.Should().BeGreaterThanOrEqualTo(0);
        cliBranch.Should().BeGreaterThan(kernelStart);
        listenerStart.Should().BeGreaterThan(cliBranch);
        hostSource.Should().Contain("RuntimeCliSession.RunAsync");
        launcherSource.Should().Contain("RuntimeLaunchPlan.From");
        launcherSource.Should().Contain("case RuntimeLaunchMode.RemoteProxy");
        launcherSource.Should().Contain("case RuntimeLaunchMode.PairingClient");
        launcherSource.Should().NotContain("RuntimeCliSession");
        launcherSource.Should().NotContain("RuntimeCliActionCatalog");
        hostProject.Should().Contain("Compile Remove=\"Cli\\**\\*.cs\"");
        adapterSource.Should().Contain("private readonly KernelActionDispatcher _actionDispatcher");
        var cliActionStart = adapterSource.IndexOf(
            "internal async ValueTask<TResult> RunCliActionAsync",
            StringComparison.Ordinal);
        cliActionStart.Should().BeGreaterThanOrEqualTo(0);
        adapterSource[cliActionStart..].Should().Contain("_actionDispatcher.RunRequiredWithContextAsync");
        adapterSource[cliActionStart..].Should().NotContain("new KernelActionDispatcher");
        sessionSource.Should().Contain("RuntimeCliActionCatalog.Parse");
        sessionSource.Should().Contain("RuntimeCliActionCatalog.CommandSelect");
        sessionSource.Should().Contain("RuntimeCliActionCatalog.Execute");
        sessionSource.Should().Contain("RuntimeCliActionCatalog.OutputWrite");
        sessionSource.Should().Contain("RuntimeCliActionCatalog.Complete");
        sessionSource.Should().Contain("RuntimeCliActionCatalog.Fail");
        sessionSource.Should().Contain("RuntimeCliActionCatalog.Cancel");
        sessionSource.Should().Contain("catch (KernelActionCancelledException)");
        sessionSource.Should().Contain(
            "cancellation => ExecuteAsync(command, kernel, cancellation)");
        hostSource.Should().Contain("CancellationToken cancellationToken = default");
        programSource.Should().Contain("Console.CancelKeyPress");
        programSource.Should().Contain("processCancellation.Token");
    }

    private static RuntimeKernelAdapter CreateAdapter(
        TemporaryWorkspace workspace,
        IServiceProvider hostServices,
        CliProbe probe)
    {
        var provider = new CliProvider(probe);
        var moduleId = "k04-cli-test";
        var grants = RuntimeCliActionCatalog.All.ToDictionary(
            action => action.Value,
            action => KernelActionCatalog.DescriptorFor(action).Capabilities,
            StringComparer.Ordinal);
        return new RuntimeKernelAdapter(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Provider:Key"] = "k04-test",
                    ["Provider:Model"] = "k04-model",
                })
                .Build(),
            hostServices,
            new InMemoryConversationStore(),
            [new CliModule(provider, probe)],
            workspace.CreateInstancePaths(),
            new CliProviderFactory(provider),
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

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "SharpClaw.Runtime")))
                return directory.FullName;
        }

        throw new AssertionException("The SharpClaw source root could not be located.");
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-k04-" + Guid.NewGuid().ToString("N"));

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

    private sealed class CliProbe
    {
        public ConcurrentQueue<CliObservation> Observations { get; } = new();

        public string? CancelAction { get; init; }

        public bool BlockChatUntilCancellation { get; init; }

        public CancellationTokenSource? ExecuteCancellationSource { get; init; }

        public TaskCompletionSource<bool> ChatStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ChatCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Actions() =>
            Observations.Select(static observation => observation.Action).ToArray();

        public void Record(ActionContext<KernelActionEnvelope> context) =>
            Observations.Enqueue(new CliObservation(
                context.ActionKey.Value,
                context.Caller.SubjectId,
                context.TraceId,
                context.IdempotencyKey,
                context.Depth));

        public void ShouldUseOneRootContext()
        {
            Observations.Should().NotBeEmpty();
            Observations.Select(static observation => observation.TraceId)
                .Distinct()
                .Should()
                .ContainSingle();
            Observations.Select(static observation => observation.IdempotencyKey)
                .Distinct()
                .Should()
                .ContainSingle();
        }
    }

    private sealed record CliObservation(
        string Action,
        string Subject,
        Guid TraceId,
        Guid IdempotencyKey,
        int Depth);

    private sealed class CliInterceptor(CliProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.Record(context);
            if (string.Equals(
                    probe.CancelAction,
                    context.ActionKey.Value,
                    StringComparison.Ordinal))
            {
                return ValueTask.FromResult<IActionOutcome<object>>(
                    control.Cancel("K04_TEST_CANCELLED", "The test action was cancelled."));
            }

            if (string.Equals(
                    context.ActionKey.Value,
                    RuntimeCliActionCatalog.Execute.Value,
                    StringComparison.Ordinal) &&
                probe.ExecuteCancellationSource is { } actionCancellation)
            {
                return control.ProceedAsync(actionCancellation.Token);
            }

            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class CliModule(
        IProviderPlugin provider,
        CliProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("k04-cli-test", "K04 CLI test", "k04");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<IProviderPlugin>(provider);
            module.Services.AddSingleton(probe);
            module.Services.AddSingleton<CliInterceptor>();
            foreach (var action in RuntimeCliActionCatalog.All)
            {
                module.Hooks.For(action).Use<CliInterceptor>(new HookOrdering(
                    $"k04-cli-{action.Value}",
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

    private sealed class CliProviderFactory(IProviderApiClient provider)
        : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins) => provider;
    }

    private sealed class CliProvider(CliProbe probe) : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => "k04-test";
        public string DisplayName => "K04 test";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public IModelCapabilityResolver Capabilities { get; } = new EmptyCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["k04-model"]);

        public async Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken cancellationToken = default)
        {
            probe.ChatStarted.TrySetResult(true);
            if (probe.BlockChatUntilCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    probe.ChatCancellationObserved.TrySetResult(true);
                    throw;
                }
            }

            return new ChatCompletionResult
            {
                Content = "k04-response",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            };
        }
    }

    private sealed class EmptyCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }
}
