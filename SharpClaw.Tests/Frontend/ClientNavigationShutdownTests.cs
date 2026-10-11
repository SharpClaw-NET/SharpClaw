using Microsoft.Extensions.Hosting;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
internal sealed class ClientNavigationShutdownTests
{
    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true)]
    public async Task CloseBeforeNavigationJoinsTheLateHostAndItsHeldRetirementAsync(bool reentrantClose, bool lateFault)
    {
        var path = Path.Combine(Path.GetTempPath(), "sharpclaw-navigation-" + Guid.NewGuid().ToString("N") + ".lock");
        var lifetime = new ClientStartupLifetime();
        var navigation = new TaskCompletionSource<IHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Controlled late Host retirement fault.");
        var retirement = new ControlledOperationGate(lateFault ? failure : null);
        LateStartupHost? host = null;
        var closedDispatcher = new ClosedStartupContext();
        var cleanupCalls = 0;
        var published = false;
        Task? reentered = null;
        Func<Task> cleanup = () => { Interlocked.Increment(ref cleanupCalls); return Task.CompletedTask; };
        var acquisition = closedDispatcher.InvokeAsync(() => lifetime.AcquireOwnedAsync(() =>
        {
            if (reentrantClose) reentered = lifetime.CloseAsync(null, cleanup);
#pragma warning disable VSTHRD003 // This test's controlled acquisition signal is retained by the startup owner before the callback runs, then explicitly completed and joined without UI dispatch.
            return navigation.Task;
#pragma warning restore VSTHRD003
        }, _ => published = true, resource => ((IAsyncDisposable)resource).DisposeAsync()));
        var close = lifetime.CloseAsync(null, cleanup);
        var repeated = lifetime.CloseAsync(null, cleanup);
        var desktop = ClientDesktopLifetime.RunAsync(static () => Task.CompletedTask,
            () => lifetime.CloseAsync(null, cleanup));
        try
        {
            AssertNavigationOwnsShutdown(lifetime, close, repeated, desktop, reentered, reentrantClose);
            AssertAdmissionClosed(lifetime);
            host = new LateStartupHost(path, retirement);
            navigation.SetResult(host);
            await retirement.WaitForEntryAsync().ConfigureAwait(false);
            AssertPhysicalRetirementPending(lifetime, acquisition, [close, repeated, desktop],
                published, Volatile.Read(ref cleanupCalls), path);
            retirement.Release();
            if (lateFault)
            {
                await AssertOriginalFailureAsync(close, failure).ConfigureAwait(false);
            }
            else
            {
                (await acquisition.ConfigureAwait(false)).Should().BeNull();
                await close.ConfigureAwait(false);
            }
            AssertHandleReleased(path);
        }
        finally
        {
            host ??= new LateStartupHost(path, retirement);
            await RetireHeldNavigationAsync(navigation, host, retirement,
                [acquisition, close, repeated, desktop], path).ConfigureAwait(false);
        }
        retirement.Calls.Should().Be(1);
        Volatile.Read(ref cleanupCalls).Should().Be(1);
        closedDispatcher.Posts.Should().Be(0, "late acquisition retirement must not require the closed UI dispatcher");
    }

    [Test]
    public async Task AnOpenAcquisitionTransfersTheHostToRequiredCleanupAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "sharpclaw-navigation-open-" + Guid.NewGuid().ToString("N") + ".lock");
        var lifetime = new ClientStartupLifetime();
        var retirement = new ControlledOperationGate();
        var host = new LateStartupHost(path, retirement);
        var rejectedRetirements = 0;
        try
        {
            var adopted = await lifetime.AcquireOwnedAsync(() => Task.FromResult<IHost>(host), _ => { }, resource =>
            {
                rejectedRetirements++;
                return ((IAsyncDisposable)resource).DisposeAsync();
            }).ConfigureAwait(false);
            adopted.Should().BeSameAs(host);
            lifetime.GetOwned<IHost>().Should().BeSameAs(host);
            var close = lifetime.CloseAsync(null, () => host.DisposeAsync().AsTask());
            await retirement.WaitForEntryAsync().ConfigureAwait(false);
            close.IsCompleted.Should().BeFalse();
            retirement.Release();
            await close.ConfigureAwait(false);
            rejectedRetirements.Should().Be(0);
            retirement.Calls.Should().Be(1);
            AssertHandleReleased(path);
        }
        finally
        {
            retirement.Release();
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(
                lifetime.CloseAsync(null, () => host.DisposeAsync().AsTask())).ConfigureAwait(false);
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(host.DisposeAsync().AsTask()).ConfigureAwait(false);
            File.Delete(path);
        }
    }

    [Test]
    public async Task AcquisitionFailureIsJoinedAndCleanupStillRunsAsync()
    {
        var lifetime = new ClientStartupLifetime();
        var navigation = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Controlled initial navigation failure.");
#pragma warning disable VSTHRD003 // This test owns the controlled navigation signal; it is explicitly faulted below and both acquisition and shutdown are joined before return.
        var acquisition = lifetime.AcquireOwnedAsync(() => navigation.Task, _ => { }, _ => ValueTask.CompletedTask);
#pragma warning restore VSTHRD003
        var cleanupCalls = 0;
        var close = lifetime.CloseAsync(null, () => { cleanupCalls++; return Task.CompletedTask; });
        try
        {
            close.IsCompleted.Should().BeFalse();
            navigation.SetException(failure);
#pragma warning disable VSTHRD003 // This test owns and explicitly settles the admitted navigation task; the close join is context-free.
            Func<Task> wait = () => close;
#pragma warning restore VSTHRD003
            var observed = await wait.Should().ThrowAsync<IOException>().ConfigureAwait(false);
            observed.Which.Should().BeSameAs(failure);
            cleanupCalls.Should().Be(1);
        }
        finally
        {
            navigation.TrySetException(failure);
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(acquisition).ConfigureAwait(false);
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(close).ConfigureAwait(false);
        }
    }

    private static void AssertAdmissionClosed(ClientStartupLifetime lifetime)
    {
        var allocations = 0;
        Action lateAcquire = () => _ = lifetime.AcquireOwnedAsync(() =>
        {
            allocations++;
            return Task.FromResult(new object());
        }, _ => { }, _ => ValueTask.CompletedTask);
        lateAcquire.Should().Throw<ObjectDisposedException>();
        allocations.Should().Be(0);
    }

    private static void AssertNavigationOwnsShutdown(ClientStartupLifetime lifetime,
        Task close, Task repeated, Task desktop, Task? reentered, bool reentrantClose)
    {
        lifetime.IsClosed.Should().BeTrue();
        repeated.Should().BeSameAs(close);
        if (reentrantClose) reentered.Should().BeSameAs(close);
        close.IsCompleted.Should().BeFalse("initial navigation has not supplied its resource-bearing Host");
        desktop.IsCompleted.Should().BeFalse();
    }

    private static void AssertPhysicalRetirementPending(ClientStartupLifetime lifetime,
        Task acquisition, Task[] closeWaiters, bool published, int cleanupCalls, string path)
    {
        published.Should().BeFalse();
        lifetime.GetOwned<IHost>().Should().BeNull();
        acquisition.IsCompleted.Should().BeFalse("late Host retirement is still physically held");
        foreach (var wait in closeWaiters) wait.IsCompleted.Should().BeFalse();
        cleanupCalls.Should().Be(0);
        Action reopen = () => { using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); };
        reopen.Should().Throw<IOException>("the held Host still owns its actual exclusive file handle");
    }

    private static async Task AssertOriginalFailureAsync(Task ownedClose, IOException expected)
    {
#pragma warning disable VSTHRD003 // The calling test owns this close task and has already released its context-free physical retirement gate before asserting its original fault.
        Func<Task> wait = () => ownedClose;
#pragma warning restore VSTHRD003
        var observed = await wait.Should().ThrowAsync<IOException>().ConfigureAwait(false);
        observed.Which.Should().BeSameAs(expected);
    }

    private static void AssertHandleReleased(string path)
    {
        using var reopened = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        reopened.CanRead.Should().BeTrue("actual Host retirement has released its exclusive handle");
    }

    private static async Task RetireHeldNavigationAsync(TaskCompletionSource<IHost> navigation,
        LateStartupHost host, ControlledOperationGate retirement, Task[] ownedWaiters, string path)
    {
        navigation.TrySetResult(host);
        retirement.Release();
        foreach (var waiter in ownedWaiters)
            await ClientApiReadinessTests.ObserveRetainedReadinessAsync(waiter).ConfigureAwait(false);
        await ClientApiReadinessTests.ObserveRetainedReadinessAsync(host.DisposeAsync().AsTask()).ConfigureAwait(false);
        File.Delete(path);
    }
}
