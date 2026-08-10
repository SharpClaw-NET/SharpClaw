using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[TestFixture]
[NonParallelizable]
public sealed class ClientActionBoundaryTests
{
    private static readonly string[] ExpectedActions =
    [
        "client.command.receive",
        "client.command.validate",
        "client.command.dispatch",
        "client.command.complete",
        "client.command.fail",
        "client.command.cancel",
        "client.navigation.prepare",
        "client.navigation.commit",
        "client.state.prepare",
        "client.state.commit",
    ];

    [Test]
    public void Catalog_matches_the_published_client_action_set()
    {
        ClientActionCatalog.All.Select(static action => action.Value)
            .Should().Equal(ExpectedActions);
        ClientActionCatalog.Coverage.Select(static entry => entry.Id)
            .Should().Equal(ExpectedActions);
        ClientActionCatalog.All.Should().OnlyContain(
            action => SharpClawActionCatalog.Kernel.Contains(action));
    }

    [Test]
    public async Task Command_runs_the_complete_lifecycle_once()
    {
        var probe = new ClientProbe();
        var dispatcher = CreateDispatcher(probe);

        var result = await dispatcher.RunCommandAsync(
            new ClientCommandInvocation("test", "GET", "/test", Guid.NewGuid()),
            (_, _) =>
            {
                Interlocked.Increment(ref probe.TerminalCalls);
                return ValueTask.FromResult("ok");
            });

        result.Should().Be("ok");
        probe.Actions().Should().Equal(ExpectedActions[..4]);
        probe.ShouldHaveOneContext();
        probe.TerminalCalls.Should().Be(1);
    }

    [Test]
    public async Task Repeat_retries_only_the_repeatable_receive_action()
    {
        var probe = new ClientProbe { RepeatAction = ClientActionCatalog.CommandReceive.Value };
        var dispatcher = CreateDispatcher(probe);

        var result = await dispatcher.RunCommandAsync(
            new ClientCommandInvocation("repeat", "GET", "/repeat", Guid.NewGuid()),
            (_, _) =>
            {
                Interlocked.Increment(ref probe.TerminalCalls);
                return ValueTask.FromResult("ok");
            });

        result.Should().Be("ok");
        probe.Attempts(ClientActionCatalog.CommandReceive.Value).Should().Be(2);
        probe.TerminalCalls.Should().Be(1);
        probe.Actions().Should().Equal(
            "client.command.receive",
            "client.command.receive",
            "client.command.validate",
            "client.command.dispatch",
            "client.command.complete");
    }

    [Test]
    public async Task ReplaceInput_changes_only_the_action_payload_seen_by_the_terminal()
    {
        var probe = new ClientProbe
        {
            ReplaceInputAction = ClientActionCatalog.CommandValidate.Value,
            Replacement = new ClientCommandInvocation(
                "allowed", "POST", "/allowed", Guid.NewGuid()),
        };
        var dispatcher = CreateDispatcher(probe);
        ClientCommandInvocation? effective = null;

        await dispatcher.RunCommandAsync(
            new ClientCommandInvocation("original", "GET", "/original", Guid.NewGuid()),
            (invocation, _) =>
            {
                effective = invocation;
                return ValueTask.FromResult("ok");
            });

        effective.Should().Be(probe.Replacement);
    }

    [Test]
    public async Task ReplaceResult_changes_the_result_without_repeating_the_terminal()
    {
        var probe = new ClientProbe
        {
            ReplaceResultAction = ClientActionCatalog.CommandDispatch.Value,
            ReplacementResult = "hook-result",
        };
        var dispatcher = CreateDispatcher(probe);

        var result = await dispatcher.RunCommandAsync(
            new ClientCommandInvocation("replace", "GET", "/replace", Guid.NewGuid()),
            (_, _) =>
            {
                Interlocked.Increment(ref probe.TerminalCalls);
                return ValueTask.FromResult("terminal-result");
            });

        result.Should().Be("hook-result");
        probe.TerminalCalls.Should().Be(0);
        probe.Actions().Should().Equal(
            "client.command.receive",
            "client.command.validate",
            "client.command.dispatch",
            "client.command.complete");
    }

    [Test]
    public async Task Typed_action_cancellation_runs_cancel_without_failure_or_terminal()
    {
        var probe = new ClientProbe { CancelAction = ClientActionCatalog.CommandDispatch.Value };
        var dispatcher = CreateDispatcher(probe);

        var action = async () => await dispatcher.RunCommandAsync(
            new ClientCommandInvocation("cancel", "GET", "/cancel", Guid.NewGuid()),
            static (_, _) => ValueTask.FromResult("late"));

        await FluentActions.Invoking(action).Should().ThrowAsync<KernelActionCancelledException>();
        probe.Actions().Should().Equal(
            "client.command.receive",
            "client.command.validate",
            "client.command.dispatch",
            "client.command.cancel");
        probe.TerminalCalls.Should().Be(0);
        probe.Actions().Should().NotContain("client.command.fail");
    }

    [Test]
    public async Task In_flight_command_cancellation_reaches_the_terminal_token()
    {
        var probe = new ClientProbe();
        var dispatcher = CreateDispatcher(probe);
        using var cancellation = new CancellationTokenSource();
        var terminalStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var command = dispatcher.RunCommandAsync(
            new ClientCommandInvocation("in-flight", "POST", "/in-flight", Guid.NewGuid()),
            async (_, token) =>
            {
                terminalStarted.SetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "late";
            },
            cancellation.Token);

        await terminalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await FluentActions.Invoking(async () => await command)
            .Should().ThrowAsync<OperationCanceledException>();
        probe.TerminalCalls.Should().Be(0);
        probe.Actions().Should().Contain("client.command.cancel");
        probe.Actions().Should().NotContain("client.command.complete");
        probe.Actions().Should().NotContain("client.command.fail");
    }

    [Test]
    public async Task Failure_runs_fail_without_completion()
    {
        var probe = new ClientProbe { FailureAction = ClientActionCatalog.CommandDispatch.Value };
        var dispatcher = CreateDispatcher(probe);

        var action = async () => await dispatcher.RunCommandAsync(
            new ClientCommandInvocation("failure", "GET", "/failure", Guid.NewGuid()),
            static (_, _) => ValueTask.FromResult("late"));

        await FluentActions.Invoking(action).Should().ThrowAsync<KernelActionFailedException>();
        probe.Actions().Should().Equal(
            "client.command.receive",
            "client.command.validate",
            "client.command.dispatch",
            "client.command.fail");
        probe.Actions().Should().NotContain("client.command.complete");
    }

    [Test]
    public async Task Concurrent_commands_keep_root_contexts_isolated()
    {
        var probe = new ClientProbe();
        var dispatcher = CreateDispatcher(probe);

        await Task.WhenAll(
            dispatcher.RunCommandAsync(
                new ClientCommandInvocation("one", "GET", "/one", Guid.NewGuid()),
                static (_, _) => ValueTask.FromResult("one")).AsTask(),
            dispatcher.RunCommandAsync(
                new ClientCommandInvocation("two", "GET", "/two", Guid.NewGuid()),
                static (_, _) => ValueTask.FromResult("two")).AsTask());

        probe.Observations.Select(static item => item.TraceId).Distinct().Should().HaveCount(2);
        probe.Observations.Select(static item => item.IdempotencyKey).Distinct().Should().HaveCount(2);
        probe.Observations.GroupBy(static item => item.TraceId)
            .Should().OnlyContain(group => group.Select(static item => item.Action)
                .OrderBy(static action => Array.IndexOf(ExpectedActions, action))
                .SequenceEqual(ExpectedActions.Take(4)));
    }

    [Test]
    public async Task Navigation_serializes_commits_and_rejects_stale_versions()
    {
        var probe = new ClientProbe();
        var dispatcher = CreateDispatcher(probe);
        var version = dispatcher.GetNavigationVersionForTest();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = dispatcher.NavigateAsync(
            "first",
            null,
            async (_, _) =>
            {
                firstStarted.SetResult(true);
                await releaseFirst.Task;
            });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = dispatcher.NavigateAsync("second", null, static (_, _) => ValueTask.CompletedTask);
        releaseFirst.SetResult(true);
        await first;

        await FluentActions.Invoking(async () => await second)
            .Should().ThrowAsync<ClientActionConflictException>();
        dispatcher.GetNavigationVersionForTest().Should().Be(version + 1);
    }

    [Test]
    public async Task State_commits_serialize_and_reject_stale_versions()
    {
        var probe = new ClientProbe();
        var dispatcher = CreateDispatcher(probe);
        var version = dispatcher.GetStateVersion("settings");
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = dispatcher.CommitStateAsync(
            "settings",
            version,
            async _ =>
            {
                firstStarted.SetResult(true);
                await releaseFirst.Task;
            });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = dispatcher.CommitStateAsync("settings", version, static _ => ValueTask.CompletedTask);
        releaseFirst.SetResult(true);
        (await first).Should().Be(version + 1);
        await FluentActions.Invoking(async () => await second)
            .Should().ThrowAsync<ClientActionConflictException>();
        dispatcher.GetStateVersion("settings").Should().Be(version + 1);
    }

    [Test]
    public void Api_stream_and_http_methods_share_the_client_command_boundary()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? FindSourceRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "SharpClaw.Client.Uno", "Services", "SharpClawApiClient.cs"));

        source.Should().Contain("PostStreamAsync");
        source.Should().Contain("GetStreamAsync");
        source.Should().Contain("SendClientCommandAsync(\"POST\", path");
        source.Should().Contain("SendClientCommandAsync(\"GET\", path");
        source.Should().Contain("_clientActions.RunCommandAsync");
        source.Should().NotContain("_http.SendAsync(request, ct)");
    }

    [Test]
    public void Client_inventory_has_no_direct_navigation_or_state_bypass()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? FindSourceRoot();
        var clientRoot = Path.Combine(root, "SharpClaw.Client.Uno");
        var sourceFiles = Directory.EnumerateFiles(clientRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("ClientNavigationService.cs", StringComparison.Ordinal))
            .ToArray();

        foreach (var sourceFile in sourceFiles)
        {
            var source = File.ReadAllText(sourceFile);
            source.Should().NotContain("navigator.NavigateRouteAsync(", sourceFile);
            source.Should().NotContain("navigator.NavigateViewModelAsync<", sourceFile);
        }

        foreach (var serviceName in new[] { "ClientSettings.cs", "AccountStore.cs", "FirstSetupMarker.cs" })
        {
            var source = File.ReadAllText(Path.Combine(clientRoot, "Services", serviceName));
            source.Should().Contain("CommitStateAsync", serviceName);
        }

        File.ReadAllText(Path.Combine(clientRoot, "Presentation", "BootModel.cs"))
            .Should().Contain("RunCommandAsync");
        File.ReadAllText(Path.Combine(clientRoot, "Presentation", "FirstSetupPage.xaml.cs"))
            .Should().Contain("client.provider.ollama.probe");
        var environmentSource = File.ReadAllText(
            Path.Combine(clientRoot, "Presentation", "EnvEditorPage.xaml.cs"));
        environmentSource.Should().Contain("client.environment.save");
        environmentSource.Should().Contain("client.environment.apply");
        environmentSource.Should().Contain("client.backend.restart");
        File.ReadAllText(Path.Combine(clientRoot, "Presentation", "SettingsPage.xaml.cs"))
            .Should().Contain("client.gateway.restart");
    }

    private static ClientActionDispatcher CreateDispatcher(ClientProbe probe)
    {
        const string moduleId = "k05-client-test";
        var grants = ClientActionCatalog.All.ToDictionary(
            action => action.Value,
            action => KernelActionCatalog.DescriptorFor(action).Capabilities,
            StringComparer.Ordinal);
        var hostServices = new ServiceCollection().BuildServiceProvider();
        return new ClientActionDispatcher(
            [new ClientModule(probe)],
            hostServices,
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
            if (Directory.Exists(Path.Combine(directory.FullName, "SharpClaw.Client.Uno")))
                return directory.FullName;
        }

        throw new AssertionException("The SharpClaw source root could not be located.");
    }

    private sealed class ClientProbe
    {
        public ConcurrentQueue<ClientObservation> Observations { get; } = new();

        public string? RepeatAction { get; init; }

        public string? ReplaceInputAction { get; init; }

        public string? ReplaceResultAction { get; init; }

        public object? ReplacementResult { get; init; }

        public ClientCommandInvocation? Replacement { get; init; }

        public string? CancelAction { get; init; }

        public string? FailureAction { get; init; }

        public int TerminalCalls;

        public IReadOnlyList<string> Actions() =>
            Observations.Select(static item => item.Action).ToArray();

        public int Attempts(string action) =>
            Observations.Count(item => item.Action == action);

        public void Record(ActionContext<KernelActionEnvelope> context) =>
            Observations.Enqueue(new ClientObservation(
                context.ActionKey.Value,
                context.TraceId,
                context.IdempotencyKey,
                context.Attempt));

        public void ShouldHaveOneContext()
        {
            Observations.Select(static item => item.TraceId).Distinct().Should().ContainSingle();
            Observations.Select(static item => item.IdempotencyKey).Distinct().Should().ContainSingle();
        }
    }

    private sealed record ClientObservation(
        string Action,
        Guid TraceId,
        Guid IdempotencyKey,
        int Attempt);

    private sealed class ClientInterceptor(ClientProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.Record(context);

            if (probe.CancelAction == context.ActionKey.Value)
                return control.Cancel("K05_TEST_CANCELLED", "The client action was cancelled.");

            if (probe.FailureAction == context.ActionKey.Value)
                throw new InvalidOperationException("K05 test failure.");

            if (probe.RepeatAction == context.ActionKey.Value && context.Attempt == 1)
                return await control.RepeatAsync(
                    new ActionRepeatRequest<KernelActionEnvelope>(
                        context.Action,
                        "K05 repeat boundary test",
                        null),
                    cancellationToken);

            if (probe.ReplaceResultAction == context.ActionKey.Value)
                return control.ReplaceResult(probe.ReplacementResult!, "K05 result boundary test");

            if (probe.ReplaceInputAction == context.ActionKey.Value && probe.Replacement is { } replacement)
                return await control.ProceedWithInputAsync(
                    new ActionReplacement<KernelActionEnvelope>(
                        context.Action with { Payload = replacement },
                        "K05 input boundary test"),
                    cancellationToken);

            return await control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ClientModule(ClientProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("k05-client-test", "K05 client test", "k05");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton(probe);
            module.Services.AddSingleton<ClientInterceptor>();
            foreach (var action in ClientActionCatalog.All)
            {
                module.Hooks.For(action).Use<ClientInterceptor>(new HookOrdering(
                    $"k05-client-{action.Value}",
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
}
