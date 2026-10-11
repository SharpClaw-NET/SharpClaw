using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Core.Kernel;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class ClientReadinessSettlementTests
{
    [TestCase(false), TestCase(true)]
    public async Task CancellationAfterEntryJoinsActualProbeAndPreservesTheReceiptAsync(bool lateFault)
    {
        using var handler = new ReadinessHttpHandler(null, ignoreCancellation: true,
            lateFailure: lateFault ? new IOException("Controlled late physical fault.") : null);
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:48923") };
        var probe = new ReadinessPhaseProbe();
        var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance,
            ClientActionDispatcher.CreateProduction(new ClientActionContextSource(), probe), fixedApiKey: "fixture-key");
        await using var apiDisposal = api.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var readiness = api.WaitForReadyAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        try
        {
            await handler.LaterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            await probe.StoppedReceipt.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            readiness.IsCompleted.Should().BeFalse("Core's interrupted receipt cannot retire an active physical probe");
            handler.Release();
#pragma warning disable VSTHRD003 // This test owns the retained readiness operation and releases/joins its context-free probe before dependencies retire.
            Func<Task> wait = () => readiness;
#pragma warning restore VSTHRD003
            var failure = await wait.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
            failure.Which.Error.Code.Should().Be("ACTION_CONTINUATION_DENIED");
            handler.Calls.Should().Be(1);
        }
        finally
        {
            handler.Release();
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(readiness).ConfigureAwait(false);
        }
    }
}
