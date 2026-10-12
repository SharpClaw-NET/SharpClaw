using System.Collections.Concurrent;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class DiscoveryCallbackOwnershipTests
{
    [TestCase(false), TestCase(true)]
    public async Task PeriodicFailureIsObservedAndRepeatedDisposeJoinsActualCallbacksAsync(bool accessDenied)
    {
        var root = Path.Combine(Path.GetTempPath(), "discovery-callback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ConcurrentQueue<Exception>();
        Exception failure = accessDenied ? new UnauthorizedAccessException("controlled periodic write denial")
            : new IOException("controlled periodic write failure");
        var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Gateway, root, root, root);
        using var lease = new SharpClawDiscoveryLease(paths, "http://127.0.0.1:1", TimeSpan.FromMilliseconds(10),
            () => { entered.TrySetResult(); release.Wait(); throw failure; }, observed.Enqueue);
        Task first = Task.CompletedTask;
        Task repeated = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            first = Task.Run(lease.Dispose, CancellationToken.None);
            repeated = Task.Run(lease.Dispose, CancellationToken.None);
            first.IsCompleted.Should().BeFalse(); repeated.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.Set();
            await Task.WhenAll(first, repeated).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            lease.Dispose();
            Directory.Delete(root, recursive: true);
        }
        observed.Should().NotBeEmpty().And.OnlyContain(exception => ReferenceEquals(exception, failure));
    }

    [Test]
    public void SynchronousInitialPublicationStillRetainsItsOriginalFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "discovery-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var failure = new IOException("controlled startup publication failure");
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Gateway, root, root, root);
            using var lease = new SharpClawDiscoveryLease(paths, "http://127.0.0.1:1", TimeSpan.FromHours(1),
                () => throw failure, _ => throw new AssertionException("Startup failure cannot be swallowed by the periodic observer."));
            Action publish = lease.PublishNow;
            publish.Should().Throw<IOException>().Which.Should().BeSameAs(failure);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
