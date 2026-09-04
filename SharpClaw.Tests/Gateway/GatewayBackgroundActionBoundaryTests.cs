using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Tests.Gateway;

[TestFixture]
public sealed class GatewayBackgroundActionBoundaryTests
{
    [Test]
    public void Manifest_matches_every_published_background_action()
    {
        GatewayBackgroundActionManifest.Required
            .Select(static key => key.Value)
            .Should()
            .Equal(
                "background.service.start",
                "background.tick.prepare",
                "background.tick.execute",
                "background.tick.complete",
                "background.tick.fail",
                "background.tick.cancel",
                "background.service.stop");
    }

    [Test]
    public async Task Boundary_routes_service_tick_and_stop_through_one_dispatcher()
    {
        var probe = new BackgroundProbe();
        var boundary = CreateBoundary(probe);
        var service = new GatewayBackgroundServiceInvocation("test-service");
        var tick = new GatewayBackgroundTickInvocation("test-service", "test-work", Guid.NewGuid());

        await boundary.StartAsync(service, CancellationToken.None);
        await boundary.ExecuteTickAsync(
            tick,
            _ =>
            {
                probe.WorkCalls++;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        await boundary.StopAsync(service, CancellationToken.None);

        probe.ActionKeys.Should().Equal(
            "background.service.start",
            "background.tick.prepare",
            "background.tick.execute",
            "background.tick.complete",
            "background.service.stop");
        probe.WorkCalls.Should().Be(1);
    }

    [Test]
    public async Task ReplaceResult_without_terminal_fails_closed_and_does_not_run_work()
    {
        var probe = new BackgroundProbe { ReplaceResultAction = "background.tick.execute" };
        var boundary = CreateBoundary(probe);
        var workCalls = 0;

        var action = () => boundary.ExecuteTickAsync(
            new GatewayBackgroundTickInvocation("test-service", "replace", Guid.NewGuid()),
            _ =>
            {
                workCalls++;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).AsTask();

        await action.Should().ThrowAsync<KernelActionExecutionException>();
        workCalls.Should().Be(0);
        probe.ActionKeys.Should().ContainInOrder(
            "background.tick.prepare",
            "background.tick.execute",
            "background.tick.fail");
    }

    [Test]
    public async Task Action_cancellation_routes_cancel_without_running_work()
    {
        var probe = new BackgroundProbe { CancelAction = "background.tick.execute" };
        var boundary = CreateBoundary(probe);
        var workCalls = 0;

        var action = () => boundary.ExecuteTickAsync(
            new GatewayBackgroundTickInvocation("test-service", "cancel", Guid.NewGuid()),
            _ =>
            {
                workCalls++;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).AsTask();

        await action.Should().ThrowAsync<KernelActionCancelledException>();
        workCalls.Should().Be(0);
        probe.ActionKeys.Should().ContainInOrder(
            "background.tick.prepare",
            "background.tick.execute",
            "background.tick.cancel");
    }

    [Test]
    public async Task Work_failure_routes_fail_once_and_does_not_complete()
    {
        var probe = new BackgroundProbe();
        var boundary = CreateBoundary(probe);
        var failure = new InvalidOperationException("test work failure");

        var action = () => boundary.ExecuteTickAsync(
            new GatewayBackgroundTickInvocation("test-service", "failure", Guid.NewGuid()),
            _ => ValueTask.FromException(failure),
            CancellationToken.None).AsTask();

        await action.Should().ThrowAsync<KernelActionFailedException>()
            .WithMessage("test work failure");
        probe.ActionKeys.Should().ContainInOrder(
            "background.tick.prepare",
            "background.tick.execute",
            "background.tick.fail");
        probe.ActionKeys.Should().NotContain("background.tick.complete");
    }

    [Test]
    public async Task Concurrent_ticks_have_isolated_action_contexts()
    {
        var probe = new BackgroundProbe();
        var boundary = CreateBoundary(probe);
        var first = boundary.ExecuteTickAsync(
            new GatewayBackgroundTickInvocation("test-service", "one", Guid.NewGuid()),
            _ => ValueTask.CompletedTask,
            CancellationToken.None).AsTask();
        var second = boundary.ExecuteTickAsync(
            new GatewayBackgroundTickInvocation("test-service", "two", Guid.NewGuid()),
            _ => ValueTask.CompletedTask,
            CancellationToken.None).AsTask();

        await Task.WhenAll(first, second);

        probe.ExecuteContexts.Should().HaveCount(2);
        probe.ExecuteContexts.Select(value => value.TraceId).Distinct().Should().HaveCount(2);
        probe.ExecuteContexts.Select(value => value.IdempotencyKey).Distinct().Should().HaveCount(2);
    }

    [Test]
    public void Queue_processor_uses_the_background_boundary_before_processing()
    {
        var sourceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "..",
                "..",
                "..",
                ".."));
        var sourcePath = Path.Combine(
            sourceRoot,
            "SharpClaw.Gateway",
            "Infrastructure",
            "RequestQueueService.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("GatewayBackgroundActionBoundary backgroundActions");
        source.Should().Contain("backgroundActions.StartAsync");
        source.Should().Contain("backgroundActions.ExecuteTickAsync");
        source.Should().Contain("backgroundActions.StopAsync");
        source.Should().NotContain("await ProcessRequestAsync(request, opts, stoppingToken)");

        var programSource = File.ReadAllText(Path.Combine(
            sourceRoot,
            "SharpClaw.Gateway",
            "Program.cs"));
        programSource.Should().Contain("AddSingleton<GatewayBackgroundActionBoundary>()");
    }

    [Test]
    public void Gateway_inventory_identifies_only_the_live_request_queue_background_service()
    {
        var sourceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "..",
                "..",
                "..",
                ".."));
        var gatewayFiles = Directory.GetFiles(
            Path.Combine(sourceRoot, "SharpClaw.Gateway"),
            "*.cs",
            SearchOption.AllDirectories);
        var backgroundServiceFiles = gatewayFiles
            .Where(path => File.ReadAllText(path).Contains(
                ": BackgroundService",
                StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .ToArray();

        backgroundServiceFiles.Should().Equal("RequestQueueService.cs");
    }

    private static GatewayBackgroundActionBoundary CreateBoundary(BackgroundProbe probe)
    {
        var actionGrants = GatewayBackgroundActionManifest.Required.ToDictionary(
            static key => key.Value,
            static key => key.Value == "background.tick.execute"
                ? ActionInterceptionCapabilities.Inspect |
                    ActionInterceptionCapabilities.Wrap |
                    ActionInterceptionCapabilities.ReplaceResult |
                    ActionInterceptionCapabilities.Cancel
                : ActionInterceptionCapabilities.Inspect |
                    ActionInterceptionCapabilities.Wrap,
            StringComparer.Ordinal);
        var graph = TestServiceGraph.Compile(
            [new BackgroundProbeRegistration(probe)],
            new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(
                StringComparer.Ordinal)
            {
                ["gateway-background-test"] = actionGrants,
            },
        });
        var dispatcher = new KernelActionDispatcher(
            graph,
            new KernelActionExecutionContext(
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid()));
        return new GatewayBackgroundActionBoundary(graph, dispatcher);
    }

    private sealed class BackgroundProbe
    {
        public ConcurrentQueue<string> ActionKeys { get; } = new();
        public ConcurrentQueue<(Guid TraceId, Guid IdempotencyKey)> ExecuteContexts { get; } = new();
        public string? ReplaceResultAction { get; init; }
        public string? CancelAction { get; init; }
        public int WorkCalls;
    }

    private sealed class BackgroundProbeRegistration(BackgroundProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("gateway-background-test", "Gateway background test", "gateway-background");

        public void ConfigureServices(IServiceCollection extension)
        {
            extension.AddSingleton(probe);
            extension.AddSingleton<BackgroundInterceptor>();
            foreach (var key in GatewayBackgroundActionManifest.Required)
            {
                extension.OnAction(key).Use<BackgroundInterceptor>(new HookOrdering(
                    $"gateway-background-{key.Value}",
                    HookPriority.Normal,
                    [],
                    [],
                    TimeSpan.FromSeconds(5),
                    HookFailurePolicy.FailAction));
            }
        }
    }

    private sealed class BackgroundInterceptor(BackgroundProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.ActionKeys.Enqueue(context.ActionKey.Value);
            if (context.ActionKey.Value == "background.tick.execute")
            {
                probe.ExecuteContexts.Enqueue((context.TraceId, context.IdempotencyKey));
                if (string.Equals(probe.ReplaceResultAction, context.ActionKey.Value, StringComparison.Ordinal))
                    return ValueTask.FromResult(control.ReplaceResult(true, "test replacement"));
                if (string.Equals(probe.CancelAction, context.ActionKey.Value, StringComparison.Ordinal))
                    return ValueTask.FromResult(control.Cancel("TEST_CANCELLED", "test cancellation"));
            }

            return control.ProceedAsync(cancellationToken);
        }
    }
}
