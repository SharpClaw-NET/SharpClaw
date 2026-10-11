using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class ClientDesktopLifetimeTests
{
    [TestCase(false), TestCase(true)]
    public async Task NativeCompletionOrFailureJoinsActualShutdownAsync(bool hostFails)
    {
        var shutdown = new ControlledOperationGate();
        var hostFailure = new IOException("Controlled native host failure.");
        var lifetime = ClientDesktopLifetime.RunAsync(
            () => hostFails ? Task.FromException(hostFailure) : Task.CompletedTask,
            () => shutdown.RunAsync().AsTask());
        try
        {
            await shutdown.WaitForEntryAsync().ConfigureAwait(false);
            lifetime.IsCompleted.Should().BeFalse("the native loop receipt cannot retire actual application cleanup");
            shutdown.Release();
            if (hostFails)
            {
#pragma warning disable VSTHRD003 // The test owns this retained desktop lifetime and releases its context-free shutdown gate before observing it.
                Func<Task> wait = () => lifetime;
#pragma warning restore VSTHRD003
                var failure = await wait.Should().ThrowAsync<IOException>().ConfigureAwait(false);
                failure.Which.Should().BeSameAs(hostFailure);
            }
            else await lifetime.ConfigureAwait(false);
            shutdown.Calls.Should().Be(1);
        }
        finally
        {
            shutdown.Release();
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(lifetime).ConfigureAwait(false);
        }
    }

    [TestCase(false), TestCase(true)]
    public async Task ShutdownFailureRemainsSecondaryToAnEarlierNativeFailureAsync(bool hostFails)
    {
        var hostFailure = new IOException("Controlled native failure.");
        var shutdownFailure = new InvalidOperationException("Controlled shutdown failure.");
        Func<Task> run = () => ClientDesktopLifetime.RunAsync(
            () => hostFails ? Task.FromException(hostFailure) : Task.CompletedTask,
            () => Task.FromException(shutdownFailure));
        var failure = await run.Should().ThrowAsync<Exception>().ConfigureAwait(false);
        failure.Which.Should().BeSameAs(hostFails ? hostFailure : shutdownFailure);
    }
}
