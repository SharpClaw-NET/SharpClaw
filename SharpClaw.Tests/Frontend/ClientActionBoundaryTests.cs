using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.Presentation;
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
        var dispatcher = CreateDispatcher(
            probe,
            repeatEvidenceAuthority: new TestRepeatEvidenceAuthority());

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
    public async Task Completed_cancelled_and_failed_commands_do_not_leak_contexts()
    {
        var probe = new ClientProbe();
        var dispatcher = CreateDispatcher(probe);

        await dispatcher.RunCommandAsync("completed", static _ => ValueTask.FromResult(true));

        probe.CancelAction = ClientActionCatalog.CommandDispatch.Value;
        await FluentActions.Invoking(async () => await dispatcher.RunCommandAsync(
                "cancelled",
                static _ => ValueTask.FromResult(true)))
            .Should().ThrowAsync<KernelActionCancelledException>();

        probe.CancelAction = null;
        probe.FailureAction = ClientActionCatalog.CommandDispatch.Value;
        await FluentActions.Invoking(async () => await dispatcher.RunCommandAsync(
                "failed",
                static _ => ValueTask.FromResult(true)))
            .Should().ThrowAsync<KernelActionFailedException>();

        probe.FailureAction = null;
        await dispatcher.RunCommandAsync("after-failure", static _ => ValueTask.FromResult(true));

        var groups = probe.Observations.GroupBy(static item => item.TraceId).ToArray();
        groups.Should().HaveCount(4);
        groups.Should().OnlyContain(group =>
            group.Select(static item => item.IdempotencyKey).Distinct().Count() == 1);
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
    public async Task Navigation_repeat_runs_the_host_terminal_once()
    {
        var probe = new ClientProbe
        {
            RepeatAction = ClientActionCatalog.NavigationCommit.Value,
        };
        var dispatcher = CreateDispatcher(
            probe,
            repeatEvidenceAuthority: new TestRepeatEvidenceAuthority());
        var terminalCalls = 0;

        await dispatcher.NavigateAsync(
            "repeat-navigation",
            null,
            (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.CompletedTask;
            });

        terminalCalls.Should().Be(1);
        probe.Attempts(ClientActionCatalog.NavigationCommit.Value).Should().Be(2);
        dispatcher.GetNavigationVersionForTest().Should().Be(1);
    }

    [Test]
    public async Task Navigation_result_replacement_cannot_claim_a_host_commit()
    {
        var probe = new ClientProbe
        {
            ReplaceResultAction = ClientActionCatalog.NavigationCommit.Value,
            ReplacementResult = new object(),
        };
        var dispatcher = CreateDispatcher(probe);
        var terminalCalls = 0;

        await FluentActions.Invoking(async () => await dispatcher.NavigateAsync(
                "replaced-navigation",
                null,
                (_, _) =>
                {
                    Interlocked.Increment(ref terminalCalls);
                    return ValueTask.CompletedTask;
                }))
            .Should().ThrowAsync<KernelActionExecutionException>();

        terminalCalls.Should().Be(0);
        dispatcher.GetNavigationVersionForTest().Should().Be(0);
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
    public async Task State_repeat_runs_the_host_terminal_once()
    {
        var probe = new ClientProbe
        {
            RepeatAction = ClientActionCatalog.StateCommit.Value,
        };
        var dispatcher = CreateDispatcher(
            probe,
            repeatEvidenceAuthority: new TestRepeatEvidenceAuthority());
        var terminalCalls = 0;
        var version = dispatcher.GetStateVersion("repeat-state");

        var committedVersion = await dispatcher.CommitStateAsync(
            "repeat-state",
            version,
            _ =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.CompletedTask;
            });

        terminalCalls.Should().Be(1);
        probe.Attempts(ClientActionCatalog.StateCommit.Value).Should().Be(2);
        committedVersion.Should().Be(version + 1);
    }

    [Test]
    public async Task State_result_replacement_cannot_claim_a_host_commit()
    {
        var probe = new ClientProbe
        {
            ReplaceResultAction = ClientActionCatalog.StateCommit.Value,
            ReplacementResult = new object(),
        };
        var dispatcher = CreateDispatcher(probe);
        var terminalCalls = 0;
        var version = dispatcher.GetStateVersion("replaced-state");

        await FluentActions.Invoking(async () => await dispatcher.CommitStateAsync(
                "replaced-state",
                version,
                _ =>
                {
                    Interlocked.Increment(ref terminalCalls);
                    return ValueTask.CompletedTask;
                }))
            .Should().ThrowAsync<KernelActionExecutionException>();

        terminalCalls.Should().Be(0);
        dispatcher.GetStateVersion("replaced-state").Should().Be(version);
    }

    [Test]
    public async Task Repeat_without_host_evidence_fails_before_the_commit_terminal()
    {
        var probe = new ClientProbe
        {
            RepeatAction = ClientActionCatalog.StateCommit.Value,
        };
        var dispatcher = CreateDispatcher(probe);
        var terminalCalls = 0;
        var version = dispatcher.GetStateVersion("unauthorized-repeat");

        await FluentActions.Invoking(async () => await dispatcher.CommitStateAsync(
                "unauthorized-repeat",
                version,
                _ =>
                {
                    Interlocked.Increment(ref terminalCalls);
                    return ValueTask.CompletedTask;
                }))
            .Should().ThrowAsync<KernelActionFailedException>();

        terminalCalls.Should().Be(0);
        dispatcher.GetStateVersion("unauthorized-repeat").Should().Be(version);
    }

    [Test]
    public async Task Production_dispatcher_composes_the_authoritative_client_module()
    {
        var source = new ClientActionContextSource();
        var dispatcher = ClientActionDispatcher.CreateProduction(source);

        dispatcher.Graph.Modules.Modules.Should().ContainSingle();
        dispatcher.Graph.Modules.Modules[0].Identity.Id.Should().Be("sharpclaw.client");

        var result = await dispatcher.RunCommandAsync(
            new ClientCommandInvocation("production", "CLIENT", "production", Guid.NewGuid()),
            static (_, _) => ValueTask.FromResult("composed"));

        result.Should().Be("composed");
    }

    [Test]
    public async Task Production_graph_keeps_callers_and_features_isolated()
    {
        var source = new ClientActionContextSource();
        var sink = new ProductionContextSink();
        var dispatcher = ClientActionDispatcher.CreateProduction(source, sink);
        using var featureDocument = JsonDocument.Parse("true");
        var featureSet = new ExtensionFeatureSet(
        [
            new ExtensionFeature(
                "client.test.feature",
                1,
                "k05-client-test",
                64,
                featureDocument.RootElement.Clone()),
        ]);

        await Task.WhenAll(
            dispatcher.RunWithContextAsync(
                new ClientActionRequestContext(
                    new RequestPrincipal("user-a", "A", new HashSet<string>(), true),
                    featureSet),
                new ClientCommandInvocation("a", "CLIENT", "a", Guid.NewGuid()),
                static (_, _) => ValueTask.FromResult(true)).AsTask(),
            dispatcher.RunWithContextAsync(
                new ClientActionRequestContext(
                    new RequestPrincipal("user-b", "B", new HashSet<string>(), true),
                    ExtensionFeatureSet.Empty),
                new ClientCommandInvocation("b", "CLIENT", "b", Guid.NewGuid()),
                static (_, _) => ValueTask.FromResult(true)).AsTask());

        sink.Observations
            .Where(static observation => observation.Action == ClientActionCatalog.CommandReceive.Value)
            .Select(static observation => observation.CallerSubjectId)
            .Should().BeEquivalentTo(["user-a", "user-b"]);
        sink.Observations
            .Where(static observation => observation.Action == ClientActionCatalog.CommandReceive.Value)
            .Should().ContainSingle(item => item.CallerSubjectId == "user-a" &&
                item.FeatureNames.Contains("client.test.feature"));
        sink.Observations
            .Where(static observation => observation.Action == ClientActionCatalog.CommandReceive.Value)
            .Should().ContainSingle(item => item.CallerSubjectId == "user-b" &&
                item.FeatureNames.Count == 0);
    }

    [Test]
    public async Task Api_client_sends_the_accepted_effective_method_and_path()
    {
        var probe = new ClientProbe
        {
            ReplaceInputAction = ClientActionCatalog.CommandValidate.Value,
            Replacement = new ClientCommandInvocation(
                "http.send",
                "PUT",
                "/accepted",
                Guid.NewGuid(),
                "/accepted"),
        };
        var dispatcher = CreateDispatcher(probe);
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var api = new SharpClawApiClient(
            http,
            NullLogger<SharpClawApiClient>.Instance,
            dispatcher,
            "test-api-key");

        using var response = await api.GetAsync("/original");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Request.Should().NotBeNull();
        handler.Request!.Method.Method.Should().Be("PUT");
        handler.Request.RequestUri!.PathAndQuery.Should().Be("/accepted");
    }

    [Test]
    public async Task Runtime_apply_retargets_after_readiness_with_one_state_transition()
    {
        const string targetA = "http://runtime-a.test:48923/";
        const string targetB = "http://runtime-b.test:48923/";
        var dispatcher = new ClientActionDispatcher();
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri(targetA) };
        using var api = new SharpClawApiClient(
            http,
            NullLogger<SharpClawApiClient>.Instance,
            dispatcher,
            "test-api-key");
        using var backend = new BackendProcessManager(
            targetA,
            NullLogger<BackendProcessManager>.Instance);
        using var gateway = new GatewayProcessManager(
            "http://gateway.test:48924/",
            targetA,
            NullLogger<GatewayProcessManager>.Instance);

        using var initialResponse = await api.GetAsync("/readyz");
        await SettingsPage.ApplyRuntimeTargetAsync(
            api,
            dispatcher,
            backend,
            gateway,
            targetB,
            TimeSpan.FromSeconds(1));
        using var retargetedResponse = await api.GetAsync("/readyz");

        handler.Requests.Select(static request => request.Uri).Should().Equal(
            "http://runtime-a.test:48923/readyz",
            "http://runtime-b.test:48923/ping",
            "http://runtime-b.test:48923/readyz");
        dispatcher.GetStateVersion("client.api.target").Should().Be(1);
        http.BaseAddress.Should().Be(targetA);
        api.BaseUrl.Should().Be(targetB);
        backend.ApiUrl.Should().Be(targetB);
        gateway.BackendBaseUrl.Should().Be(targetB);
    }

    [Test]
    public async Task Boot_retry_retargets_after_a_failed_probe()
    {
        const string targetA = "http://runtime-a.test:48923/";
        const string targetB = "http://runtime-b.test:48923/";
        var dispatcher = new ClientActionDispatcher();
        var handler = new CapturingHandler
        {
            ResponseFactory = request => new HttpResponseMessage(
                request.RequestUri!.Host == "runtime-a.test"
                    ? HttpStatusCode.ServiceUnavailable
                    : HttpStatusCode.OK),
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(targetA) };
        using var api = new SharpClawApiClient(
            http,
            NullLogger<SharpClawApiClient>.Instance,
            dispatcher,
            "test-api-key");
        using var backend = new BackendProcessManager(
            targetA,
            NullLogger<BackendProcessManager>.Instance,
            frontendInstance: null,
            executablePath: Path.Combine(Path.GetTempPath(), "unused-runtime-host"),
            processOnPortProbe: () => false,
            apiReachabilityProbe: _ => Task.FromResult(true),
            processStartObserver: null);
        using var gateway = new GatewayProcessManager(
            "http://gateway.test:48924/",
            targetA,
            NullLogger<GatewayProcessManager>.Instance);
        var boot = new BootModel(backend, gateway, api, null, dispatcher);

        (await boot.RunBackendStepAsync(CancellationToken.None)).Ok.Should().BeTrue();
        using var failedResponse = await api.GetAsync("/echo");
        failedResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        await boot.ApplyCustomUrlAsync(targetB);
        var retry = await boot.RunEchoStepAsync(CancellationToken.None);

        retry.Ok.Should().BeTrue();
        handler.Requests.Select(static request => request.Uri).Should().Equal(
            "http://runtime-a.test:48923/echo",
            "http://runtime-b.test:48923/echo");
        api.BaseUrl.Should().Be(targetB);
        backend.ApiUrl.Should().Be(targetB);
        gateway.BackendBaseUrl.Should().Be(targetB);
    }

    [Test]
    public async Task Stream_command_stays_open_until_consumption_cancellation()
    {
        var probe = new ClientProbe();
        var dispatcher = CreateDispatcher(probe);
        var handler = new CapturingHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream()),
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var api = new SharpClawApiClient(
            http,
            NullLogger<SharpClawApiClient>.Instance,
            dispatcher,
            "test-api-key");
        using var cancellation = new CancellationTokenSource();

        var operation = api.ConsumeStreamAsync(
            "GET",
            "/stream",
            null,
            async (response, token) =>
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                await stream.ReadExactlyAsync(new byte[1], token);
            },
            cancellation.Token);

        await Task.Delay(50);
        cancellation.Cancel();

        await FluentActions.Invoking(async () => await operation)
            .Should().ThrowAsync<OperationCanceledException>();
        probe.Actions().Should().Contain("client.command.cancel");
        probe.Actions().Should().NotContain("client.command.complete");
    }

    [Test]
    public async Task Stream_command_failure_is_inside_the_command_boundary()
    {
        var probe = new ClientProbe();
        var dispatcher = CreateDispatcher(probe);
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var api = new SharpClawApiClient(
            http,
            NullLogger<SharpClawApiClient>.Instance,
            dispatcher,
            "test-api-key");

        await FluentActions.Invoking(async () => await api.ConsumeStreamAsync(
                "GET",
                "/stream",
                null,
                static (_, _) => throw new InvalidOperationException("stream failure")))
            .Should().ThrowAsync<KernelActionFailedException>();

        probe.Actions().Should().Contain("client.command.fail");
        probe.Actions().Should().NotContain("client.command.complete");
    }

    [Test]
    public void Api_stream_and_http_methods_share_the_client_command_boundary()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? FindSourceRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "SharpClaw.Client.Uno", "Services", "SharpClawApiClient.cs"));

        source.Should().Contain("ConsumeStreamAsync");
        source.Should().Contain("ResponseHeadersRead");
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

        File.ReadAllText(Path.Combine(clientRoot, "Presentation", "BootModel.cs"))
            .Should().Contain("RunCommandAsync");
        var settingsSource = File.ReadAllText(
            Path.Combine(clientRoot, "Presentation", "SettingsPage.xaml.cs"));
        settingsSource.Should().Contain("client.gateway.restart");
        settingsSource.Should().Contain("client.runtime.target");
        settingsSource.Should().NotContain("/agents");
        settingsSource.Should().NotContain("/roles");
        settingsSource.Should().NotContain("PermissionEditorBuilder");
        settingsSource.Should().NotContain("Assign Role");
    }

    [Test]
    public void Client_inventory_covers_pages_contributions_and_streams()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? FindSourceRoot();
        var clientRoot = Path.Combine(root, "SharpClaw.Client.Uno");
        var requiredSource = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["App.xaml.cs"] = [
                "ClientActionDispatcher.CreateProduction",
                "client.app.close",
            ],
            [Path.Combine("Presentation", "BootModel.cs")] = [
                "client.backend.start",
                "client.gateway.start",
                "_gateway.ApiKey = _api.CachedApiKey",
                "ClientActionDispatcher",
            ],
            [Path.Combine("Presentation", "ShellModel.cs")] = [
                "public sealed class ShellModel",
            ],
            [Path.Combine("Presentation", "MainPage.Chat.cs")] = [
                "ConsumeStreamAsync",
                "CommitStateAsync",
                "\"/chat/stream\"",
            ],
            [Path.Combine("Presentation", "MainPage.xaml.cs")] = [
                "CommitStateAsync",
                "client.chat.ui",
            ],
            [Path.Combine("Presentation", "BootPage.xaml.cs")] = [
                "NavigateRouteAsync(this, \"Main\"",
            ],
            [Path.Combine("Presentation", "SettingsPage.xaml.cs")] = [
                "Actions.RunCommandAsync",
                "client.gateway.restart",
                "client.gateway.logs.clear",
                "client.process.persistence",
                "client.runtime.target",
            ],
            [Path.Combine("Services", "SharpClawApiClient.cs")] = [
                "_clientActions.RunCommandAsync",
                "ConsumeStreamAsync",
            ],
            [Path.Combine("Services", "ClientNavigationService.cs")] = [
                "actions.NavigateAsync",
                "navigator.NavigateRouteAsync",
                "navigator.NavigateViewModelAsync",
            ],
        };

        foreach (var requirement in requiredSource)
        {
            var source = File.ReadAllText(Path.Combine(clientRoot, requirement.Key));
            foreach (var marker in requirement.Value)
                source.Should().Contain(marker, requirement.Key);
        }

        var allClientSource = Directory.EnumerateFiles(clientRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();
        string combinedSource = string.Join(Environment.NewLine, allClientSource);
        combinedSource.Should().NotContain("PostStreamAsync");
        combinedSource.Should().NotContain("GetStreamAsync");
        combinedSource.Should().NotContain("new ClientActionDispatcher([]");
        combinedSource.Should().NotContain("IAuthenticationService");
        combinedSource.Should().NotContain("UseAuthentication");
        combinedSource.Should().NotContain("/auth/login");
        combinedSource.Should().NotContain("/auth/register");
        combinedSource.Should().NotContain("/auth/refresh");
        combinedSource.Should().NotContain("/auth/me");
        combinedSource.Should().NotContain("SetAccessTokenAsync");
        combinedSource.Should().NotContain("ForAuthenticatedUser");

        foreach (var path in new[]
        {
            Path.Combine(clientRoot, "Presentation", "MainPage.xaml"),
            Path.Combine(clientRoot, "Presentation", "MainPage.xaml.cs"),
            Path.Combine(clientRoot, "Presentation", "MainPage.Chat.cs"),
            Path.Combine(clientRoot, "Presentation", "MainPage.Navigation.cs"),
            Path.Combine(clientRoot, "Presentation", "UnoClientState.cs"),
        })
        {
            var source = File.ReadAllText(path);
            source.Should().NotContain("/channels", path);
            source.Should().NotContain("/channel-contexts", path);
            source.Should().NotContain("AgentSelector", path);
            source.Should().NotContain("ThreadSelector", path);
            source.Should().NotContain("JobViewPanel", path);
            source.Should().NotContain("ChannelCost", path);
            source.Should().NotContain("ChatMessageDto", path);
        }

        foreach (var path in new[]
        {
            Path.Combine("Presentation", "LoginModel.cs"),
            Path.Combine("Presentation", "LoginPage.xaml"),
            Path.Combine("Presentation", "LoginPage.xaml.cs"),
            Path.Combine("Presentation", "FirstSetupPage.xaml"),
            Path.Combine("Presentation", "FirstSetupPage.xaml.cs"),
            Path.Combine("Presentation", "EnvMenuPage.xaml"),
            Path.Combine("Presentation", "EnvMenuPage.xaml.cs"),
            Path.Combine("Presentation", "EnvEditorPage.xaml"),
            Path.Combine("Presentation", "EnvEditorPage.xaml.cs"),
            Path.Combine("Services", "AccountStore.cs"),
            Path.Combine("Services", "ClientSessionService.cs"),
            Path.Combine("Services", "FirstSetupMarker.cs"),
            Path.Combine("Services", "CoreEnvGuard.cs"),
            Path.Combine("Services", "ModuleStateCache.cs"),
            Path.Combine("Services", "ModuleFrontendStateService.cs"),
        })
        {
            File.Exists(Path.Combine(clientRoot, path)).Should().BeFalse(path);
        }
        File.Exists(Path.Combine(clientRoot, "Presentation", "MainPage.Contributions.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "ChatActionContributionBuilders.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "SettingsPage.Contributions.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "SettingsContributionBuilders.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Services", "ModuleFrontendContributionRegistry.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "MainModel.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "SecondModel.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "DashboardPage.xaml"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "DashboardPage.xaml.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "SecondPage.xaml"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "SecondPage.xaml.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "MainPage.ChannelSettings.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Presentation", "MainPage.Jobs.cs"))
            .Should().BeFalse();
        File.Exists(Path.Combine(clientRoot, "Helpers", "PermissionEditorBuilder.cs"))
            .Should().BeFalse();
        File.ReadAllText(Path.Combine(clientRoot, "Presentation", "MainPage.Navigation.cs"))
            .Should().NotContain("AssignRole");
        File.ReadAllText(Path.Combine(clientRoot, "Presentation", "MainPage.Chat.cs"))
            .Should().NotContain("ContextFlyout");
        File.ReadAllText(Path.Combine(clientRoot, "Presentation", "MainPage.xaml.cs"))
            .Should().NotContain("TabSettingsButton");
    }

    private static ClientActionDispatcher CreateDispatcher(
        ClientProbe probe,
        ClientActionContextSource? contextSource = null,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null)
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
            },
            contextSource,
            repeatEvidenceAuthority);
    }

    private static string FindSourceRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot) &&
            Directory.Exists(Path.Combine(configuredRoot, "SharpClaw.Client.Uno")))
            return configuredRoot;

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

        public string? CancelAction { get; set; }

        public string? FailureAction { get; set; }

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
                context.Attempt,
                context.Caller.SubjectId,
                context.Features.Items.Select(static item => item.ContractName).ToArray()));

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
        int Attempt,
        string CallerSubjectId,
        IReadOnlyList<string> FeatureNames);

    private sealed class ProductionContextSink : ClientActionModuleSet.IClientActionContextSink
    {
        public ConcurrentQueue<ClientObservation> Observations { get; } = new();

        public void Observe(ActionContext<KernelActionEnvelope> context) =>
            Observations.Enqueue(new ClientObservation(
                context.ActionKey.Value,
                context.TraceId,
                context.IdempotencyKey,
                context.Attempt,
                context.Caller.SubjectId,
                context.Features.Items.Select(static item => item.ContractName).ToArray()));
    }

    private sealed class TestRepeatEvidenceAuthority : IKernelActionRepeatEvidenceAuthority
    {
        public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
            KernelActionRepeatEvidenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var issuedAt = DateTimeOffset.UtcNow;
            return ValueTask.FromResult<KernelActionRepeatEvidence?>(new(
                "K05_TEST_EVIDENCE",
                request.RequiredKind,
                request.ActionKey,
                request.ActionVersion,
                request.IdempotencyScope,
                request.IdempotencyKey,
                request.PriorInvocationId,
                request.PriorAttempt,
                request.NextInvocationId,
                request.NextAttempt,
                issuedAt,
                issuedAt.AddMinutes(1)));
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public ConcurrentQueue<(string Method, string Uri)> Requests { get; } = new();

        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; init; }

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>?
            AsyncResponseFactory
        { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Requests.Enqueue((request.Method.Method, request.RequestUri!.AbsoluteUri));
            if (AsyncResponseFactory is not null)
                return AsyncResponseFactory(request, cancellationToken);

            return Task.FromResult(
                ResponseFactory?.Invoke(request) ??
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ok"),
                });
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

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
