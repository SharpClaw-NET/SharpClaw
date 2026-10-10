using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Tests.Gateway;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class GatewayBackgroundActionBoundaryTests
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
    public async Task Boundary_routes_service_tick_and_stop_through_one_dispatcherAsync()
    {
        var probe = new BackgroundProbe();
        var boundary = CreateBoundary(probe);
        var service = new GatewayBackgroundServiceInvocation("test-service");
        var tick = new GatewayBackgroundTickInvocation("test-service", "test-work", Guid.NewGuid());

        await boundary.StartAsync(service, CancellationToken.None).ConfigureAwait(false);
        await boundary.ExecuteTickAsync(
            tick,
            _ =>
            {
                probe.WorkCalls++;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
        await boundary.StopAsync(service, CancellationToken.None).ConfigureAwait(false);

        probe.ActionKeys.Should().Equal(
            "background.service.start",
            "background.tick.prepare",
            "background.tick.execute",
            "background.tick.complete",
            "background.service.stop");
        probe.WorkCalls.Should().Be(1);
    }

    [Test]
    public async Task ReplaceResult_without_terminal_fails_closed_and_does_not_run_workAsync()
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

        await action.Should().ThrowAsync<KernelActionExecutionException>().ConfigureAwait(false);
        workCalls.Should().Be(0);
        probe.ActionKeys.Should().ContainInOrder(
            "background.tick.prepare",
            "background.tick.execute",
            "background.tick.fail");
    }

    [Test]
    public async Task Action_cancellation_routes_cancel_without_running_workAsync()
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

        await action.Should().ThrowAsync<KernelActionCancelledException>().ConfigureAwait(false);
        workCalls.Should().Be(0);
        probe.ActionKeys.Should().ContainInOrder(
            "background.tick.prepare",
            "background.tick.execute",
            "background.tick.cancel");
    }

    [Test]
    public async Task Work_failure_routes_fail_once_and_does_not_completeAsync()
    {
        var probe = new BackgroundProbe();
        var boundary = CreateBoundary(probe);
        var failure = new InvalidOperationException("test work failure");

        var action = () => boundary.ExecuteTickAsync(
            new GatewayBackgroundTickInvocation("test-service", "failure", Guid.NewGuid()),
            _ => ValueTask.FromException(failure),
            CancellationToken.None).AsTask();

        await action.Should().ThrowAsync<KernelActionFailedException>()
            .WithMessage("test work failure").ConfigureAwait(false);
        probe.ActionKeys.Should().ContainInOrder(
            "background.tick.prepare",
            "background.tick.execute",
            "background.tick.fail");
        probe.ActionKeys.Should().NotContain("background.tick.complete");
    }

    [Test]
    public async Task Concurrent_ticks_have_isolated_action_contextsAsync()
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

        await Task.WhenAll(first, second).ConfigureAwait(false);

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
            "RequestQueueProcessor.cs");
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

        backgroundServiceFiles.Should().Equal("RequestQueueProcessor.cs");
    }

    [Test]
    public async Task QueueShutdownJoinsCancelledChildrenBeforeStoppingItsActionServiceAsync()
    {
        var options = Options.Create(new RequestQueueOptions { MaxConcurrency = 2, MaxRetries = 0 });
        using var handler = new HeldCancellationHandler();
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.invalid") };
        var core = new InternalApiClient(http, Options.Create(new InternalApiOptions { ApiKey = "fixture-key" }),
            new HttpContextAccessor(), NullLogger<InternalApiClient>.Instance);
        using var queue = new RequestQueueService(options, new QueueMetrics(), NullLogger<RequestQueueService>.Instance);
        var probe = new BackgroundProbe();
        using var processor = new RequestQueueProcessor(queue, core, options, NullLogger<RequestQueueProcessor>.Instance,
            CreateBoundary(probe));
        var first = new QueuedRequest { Method = HttpMethod.Post, Path = "/first" };
        var second = new QueuedRequest { Method = HttpMethod.Post, Path = "/second" };
        var waiting = new QueuedRequest { Method = HttpMethod.Post, Path = "/waiting" };
        queue.TryEnqueue(first).Should().BeTrue();
        queue.TryEnqueue(second).Should().BeTrue();
        queue.TryEnqueue(waiting).Should().BeTrue();
        await processor.StartAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        Task stopping = Task.CompletedTask;
        try
        {
            await handler.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            queue.PendingCount.Should().Be(1, "the third request waits behind the two controlled HTTP operations");
            stopping = processor.StopAsync(CancellationToken.None);
            await handler.Cancelled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            stopping.IsCompleted.Should().BeFalse("both child cancellation callbacks still own their HTTP operations");
            probe.ActionKeys.Should().NotContain("background.service.stop");
        }
        finally
        {
            handler.Release();
            var stopOutcomes = await TestTaskOutcome.JoinAsync(
                stopping,
                processor.StopAsync(CancellationToken.None)).ConfigureAwait(false);
            stopOutcomes.Should().OnlyContain(static exception => object.Equals(exception, null));
            // These queue-owned receipts use asynchronous continuations. Both HTTP
            // gates and shutdown receipts have settled before this context-free join.
#pragma warning disable VSTHRD003
            await TestTaskOutcome.JoinAsync(first.Completion.Task, second.Completion.Task, waiting.Completion.Task).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        handler.Settled.Should().Be(2);
        first.Completion.Task.IsCanceled.Should().BeTrue();
        second.Completion.Task.IsCanceled.Should().BeTrue();
        waiting.Completion.Task.IsCanceled.Should().BeTrue();
        handler.HttpStarts.Should().Be(2, "shutdown must cancel waiting receipts without starting another HTTP operation");
        probe.ActionKeys.Should().Contain("background.service.stop");
    }

    private sealed class HeldCancellationHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedCount;
        private int _cancelledCount;
        private int _settled;
        public Task Started => _started.Task;
        public Task Cancelled => _cancelled.Task;
        public int Settled => Volatile.Read(ref _settled);
        public int HttpStarts => Volatile.Read(ref _startedCount);
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _startedCount) == 2) _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new AssertionException("The controlled HTTP operation must be cancelled by processor shutdown.");
            }
            finally
            {
                if (Interlocked.Increment(ref _cancelledCount) == 2) _cancelled.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref _settled);
            }
        }
    }

    private static GatewayBackgroundActionBoundary CreateBoundary(BackgroundProbe probe)
    {
        var actionGrants = GatewayBackgroundActionManifest.Required.ToDictionary(
            static key => key.Value,
            static key => string.Equals(key.Value, "background.tick.execute", StringComparison.Ordinal) ? ActionInterceptionCapabilities.Inspect |
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
            if (string.Equals(context.ActionKey.Value, "background.tick.execute", StringComparison.Ordinal))
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
