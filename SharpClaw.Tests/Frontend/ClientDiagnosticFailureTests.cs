using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Core.Kernel;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
internal sealed class ClientDiagnosticFailureTests
{
    [TestCase("io"), TestCase("access"), TestCase("disposed")]
    public void JournalAndFallbackSinkFailuresAreBestEffort(string failureKind)
    {
        using var sinks = new FailingStartupDiagnosticsScope(failureKind);
        Action record = () => sinks.Diagnostics.Record(ClientStartupStage.StartupFailed,
            new InvalidOperationException("Primary startup fault."));
        record.Should().NotThrow();
        sinks.FallbackCalls.Should().Be(1, "the failed journal attempted the independently failed fallback");
    }

    [TestCase("io"), TestCase("access"), TestCase("disposed")]
    public async Task BrokenSinksCannotReplaceReadinessCancellationOrItsRequiredPhysicalJoinAsync(string failureKind)
    {
        using var sinks = new FailingStartupDiagnosticsScope(failureKind);
        using var handler = new ReadinessHttpHandler(null, ignoreCancellation: true,
            lateFailure: new IOException("Controlled late physical fault."));
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:48923") };
        var probe = new ReadinessPhaseProbe();
        var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance,
            ClientActionDispatcher.CreateProduction(new ClientActionContextSource(), probe),
            fixedApiKey: "fixture-key", startupDiagnostics: sinks.Diagnostics);
        await using var apiDisposal = api.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var readiness = api.WaitForReadyAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        try
        {
            await handler.LaterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            await probe.StoppedReceipt.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            readiness.IsCompleted.Should().BeFalse();
            handler.Release();
#pragma warning disable VSTHRD003 // This retained readiness operation joins the released context-free physical HTTP task before test transport disposal.
            Func<Task> wait = () => readiness;
#pragma warning restore VSTHRD003
            var failure = await wait.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
            failure.Which.Error.Code.Should().Be("ACTION_CONTINUATION_DENIED");
            sinks.FallbackCalls.Should().BeGreaterThan(0);
        }
        finally
        {
            handler.Release();
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(readiness).ConfigureAwait(false);
        }
    }

    [TestCase("io"), TestCase("access"), TestCase("disposed")]
    public async Task BrokenSinksCannotRetireAnUnsettledMutationOrReplaceItsReceiptAsync(string failureKind)
    {
        using var sinks = new FailingStartupDiagnosticsScope(failureKind);
        var coordinator = new BundledBackendMutationCoordinator(diagnostics: sinks.Diagnostics);
        await using var coordinatorDisposal = coordinator.ConfigureAwait(false);
        var physical = new ControlledOperationGate(new IOException("Controlled late mutation fault."));
        var probe = new ReadinessPhaseProbe();
        var actions = ClientActionDispatcher.CreateProduction(new ClientActionContextSource(), probe);
        using var cancellation = new CancellationTokenSource();
        var change = coordinator.RunAsync(actions, "http.send", async _ =>
        {
            await physical.RunAsync().ConfigureAwait(false);
            return true;
        }, cancellation.Token);
        Task? close = null;
        Task? repeated = null;
        try
        {
            await physical.WaitForEntryAsync().ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            await probe.StoppedReceipt.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            close = coordinator.DisposeAsync().AsTask();
            repeated = coordinator.DisposeAsync().AsTask();
            change.IsCompleted.Should().BeFalse();
            close.IsCompleted.Should().BeFalse();
            repeated.Should().BeSameAs(close);
            physical.Release();
#pragma warning disable VSTHRD003 // The retained mutation is owned by the coordinator; this test releases and joins its context-free physical work before dependency retirement.
            Func<Task> wait = () => change;
#pragma warning restore VSTHRD003
            var failure = await wait.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
            failure.Which.Error.Code.Should().Be("ACTION_CONTINUATION_DENIED");
            await close.ConfigureAwait(false);
            sinks.FallbackCalls.Should().BeGreaterThan(1);
        }
        finally
        {
            physical.Release();
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(change).ConfigureAwait(false);
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(close ?? coordinator.DisposeAsync().AsTask()).ConfigureAwait(false);
            if (repeated is not null) await ClientApiReadinessTests.ObserveRetainedReadinessAsync(repeated).ConfigureAwait(false);
        }
    }

    [TestCase("io"), TestCase("access"), TestCase("disposed")]
    public async Task BrokenSinksPreserveTheSameCoreReceiptAndJoinLaterCleanupAsync(string failureKind)
    {
        using var sinks = new FailingStartupDiagnosticsScope(failureKind);
        var probe = new ConfigurationOperationProbe("client.app.close");
        var actions = probe.CreateDispatcher();
        Func<Task> reject = async () => await actions.RunCommandAsync("client.app.close",
            _ => ValueTask.CompletedTask, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var receipt = await reject.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
        var original = receipt.Which;
        var later = new ControlledOperationGate();
        var attempted = 0;
        async Task CleanupAsync()
        {
            Interlocked.Increment(ref attempted);
            var first = await ClientShutdownCoordinator.AttemptStepAsync(
                () => ValueTask.FromException(original), null, sinks.Diagnostics).ConfigureAwait(false);
            Interlocked.Increment(ref attempted);
            first = await ClientShutdownCoordinator.AttemptStepAsync(
                () => ValueTask.FromException(new IOException("Secondary physical cleanup fault.")),
                first, sinks.Diagnostics).ConfigureAwait(false);
            Interlocked.Increment(ref attempted);
            first = await ClientShutdownCoordinator.AttemptStepAsync(later.RunAsync, first, sinks.Diagnostics).ConfigureAwait(false);
            first?.Throw();
        }
        var owner = new ClientShutdownCoordinator(sinks.Diagnostics);
        var close = owner.ShutdownAsync(null, null, CleanupAsync);
        var repeated = owner.ShutdownAsync(null, null, CleanupAsync);
        try
        {
            await later.WaitForEntryAsync().ConfigureAwait(false);
            Volatile.Read(ref attempted).Should().Be(3);
            close.IsCompleted.Should().BeFalse("the failed diagnostic sink cannot skip the later held physical cleanup");
            repeated.Should().BeSameAs(close);
            later.Release();
#pragma warning disable VSTHRD003 // The cached context-free close operation is owned by this test and its final physical step has been explicitly released.
            Func<Task> wait = () => close;
            Func<Task> waitAgain = () => repeated;
#pragma warning restore VSTHRD003
            var failure = await wait.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
            failure.Which.Should().BeSameAs(original);
            failure.Which.Error.Should().BeSameAs(probe.Rejection);
            var again = await waitAgain.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
            again.Which.Should().BeSameAs(original);
            sinks.FallbackCalls.Should().BeGreaterThan(1, "both secondary cleanup and final settlement observation attempted failed diagnostic sinks");
        }
        finally
        {
            later.Release();
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(close).ConfigureAwait(false);
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(repeated).ConfigureAwait(false);
        }
    }
}
