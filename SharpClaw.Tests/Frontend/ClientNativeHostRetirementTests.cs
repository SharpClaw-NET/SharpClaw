using Microsoft.Extensions.Logging;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class ClientNativeHostRetirementTests
{
    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true)]
    public async Task WindowCloseRetainsLoggingUntilActualNativeSettlementAsync(bool nativeFails, bool retirementFails)
    {
        var nativeFailure = new IOException("Controlled native-loop failure.");
        var retirementFailure = new InvalidOperationException("Controlled Host retirement failure.");
        var native = new ControlledOperationGate(nativeFails ? nativeFailure : null);
        var retirement = new ControlledOperationGate(retirementFails ? retirementFailure : null);
        using var logging = LoggerFactory.Create(static _ => { });
        var desktop = new ClientDesktopLifetime();
        var owner = new ClientShutdownCoordinator();
        var context = new ClosedStartupContext();
        var close = owner.ShutdownAsync(null, null, () => context.InvokeAsync(async () =>
        {
            await desktop.RetireAfterHostAsync(() => RetireLoggingAsync(logging, retirement)).ConfigureAwait(false);
            return true;
        }));
#pragma warning disable VSTHRD003 // Window close and desktop exit join this same cached, context-free shutdown; the test releases both actual native and retirement gates before final observation.
        var lifetime = desktop.RunHostAsync(() => native.RunAsync().AsTask(), () => close);
#pragma warning restore VSTHRD003
        try
        {
            await native.WaitForEntryAsync().ConfigureAwait(false);
            AssertPending(close, lifetime);
            retirement.Calls.Should().Be(0, "a closed window cannot dispose a Host still used by the native loop");
            logging.Invoking(factory => factory.CreateLogger("NativeLoopClosing")).Should().NotThrow();
            owner.ShutdownAsync(null, null, static () => Task.CompletedTask).Should().BeSameAs(close);
            native.Release();
            await retirement.WaitForEntryAsync().ConfigureAwait(false);
            AssertPending(close, lifetime);
            retirement.Release();
            await AssertOutcomeAsync(lifetime, nativeFails ? nativeFailure : retirementFails ? retirementFailure : null)
                .ConfigureAwait(false);
            logging.Invoking(factory => factory.CreateLogger("AfterPhysicalRetirement")).Should().Throw<ObjectDisposedException>();
            retirement.Calls.Should().Be(1);
            context.Posts.Should().Be(0);
        }
        finally
        {
            native.Release();
            retirement.Release();
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(lifetime).ConfigureAwait(false);
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(close).ConfigureAwait(false);
        }
    }

    private static void AssertPending(Task close, Task lifetime)
    {
        close.IsCompleted.Should().BeFalse();
        lifetime.IsCompleted.Should().BeFalse();
    }

    private static async ValueTask RetireLoggingAsync(ILoggerFactory logging, ControlledOperationGate retirement)
    {
        try { await retirement.RunAsync().ConfigureAwait(false); }
        finally { logging.Dispose(); }
    }

    private static async Task AssertOutcomeAsync(Task lifetime, Exception? expected)
    {
#pragma warning disable VSTHRD003 // The caller owns this actual desktop task and has released both native and physical cleanup gates before inspecting its original failure.
        if (expected is null) { await lifetime.ConfigureAwait(false); return; }
        Func<Task> wait = () => lifetime;
#pragma warning restore VSTHRD003
        var failure = await wait.Should().ThrowAsync<Exception>().ConfigureAwait(false);
        failure.Which.Should().BeSameAs(expected);
    }
}
