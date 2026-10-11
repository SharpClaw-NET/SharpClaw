using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
[NonParallelizable]
internal sealed class ClientStartupOwnershipTests
{
    [TestCase(false), TestCase(true)]
    public async Task EarlyCloseJoinsAnAdmittedFactoryAndCleansItsLateOwnerBeforeCompletingAsync(bool actionsAvailable)
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        var lifetime = new ClientStartupLifetime();
        lifetime.SetMutationOwner(scope.Frontend.BackendMutations);
        using var gate = new ControlledStartupFactory();
        var path = Path.Combine(scope.BackendPaths.ConfigDirectory, "startup-resource.lock");
        var creation = Task.Run(() => lifetime.CreateOwned(() => gate.Create(() => new OwnedFileStream(path))), CancellationToken.None);
        OwnedFileStream? created = null;
        var cleanupCalls = 0;
        Func<Task> cleanup = async () =>
        {
            Interlocked.Increment(ref cleanupCalls);
            if (lifetime.GetOwned<OwnedFileStream>() is { } owned)
                await owned.DisposeAsync().ConfigureAwait(false);
        };
        try
        {
            try
            {
                await gate.WaitForEntryAsync().ConfigureAwait(false);
                var close = lifetime.CloseAsync(actionsAvailable ? scope.Actions : null, cleanup);
                var repeated = lifetime.CloseAsync(actionsAvailable ? scope.Actions : null, cleanup);
                lifetime.IsClosed.Should().BeTrue();
                close.IsCompleted.Should().BeFalse();
                repeated.IsCompleted.Should().BeFalse();
                Volatile.Read(ref cleanupCalls).Should().Be(0);
                AssertPublicationAndFactoryAdmissionAreClosed(lifetime);
                Func<Task> lateChange = () => scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, null,
                    TestContext.CurrentContext.CancellationToken);
                await lateChange.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
                try { created = await creation.ConfigureAwait(false); }
                finally { await lifetime.CloseAsync(actionsAvailable ? scope.Actions : null, cleanup).ConfigureAwait(false); }
            }
            created.Should().NotBeNull();
            created!.Disposals.Should().Be(1);
            created.CanRead.Should().BeFalse();
            Volatile.Read(ref cleanupCalls).Should().Be(1);
            lifetime.GetOwned<OwnedFileStream>().Should().BeSameAs(created);
            using var reopened = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            reopened.CanWrite.Should().BeTrue("shutdown has released the actual exclusive handle");
            scope.BackendStarts.Should().Be(0);
            scope.PeerRequests.Should().BeEmpty();
        }
        finally
        {
            // Verification failure must also retire any handle that a broken startup owner abandoned.
            if (created is { Disposals: 0 }) await created.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task AnAdmittedFactoryFailureSettlesEarlyCloseAndPreservesItsOriginalErrorAsync()
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        var lifetime = new ClientStartupLifetime();
        lifetime.SetMutationOwner(scope.Frontend.BackendMutations);
        var failure = new IOException("Controlled startup factory failure.");
        using var gate = new ControlledStartupFactory(failure);
        var creation = Task.Run(() => lifetime.CreateOwned(() => gate.Create(static () => new object())), CancellationToken.None);
        var cleanupCalls = 0;
        Func<Task> cleanup = () => { Interlocked.Increment(ref cleanupCalls); return Task.CompletedTask; };
        Exception? observed = null;
        try
        {
            await gate.WaitForEntryAsync().ConfigureAwait(false);
            var close = lifetime.CloseAsync(scope.Actions, cleanup);
            close.IsCompleted.Should().BeFalse();
        }
        finally
        {
            gate.Release();
            try { _ = await creation.ConfigureAwait(false); }
            catch (IOException exception) { observed = exception; }
            finally { await lifetime.CloseAsync(scope.Actions, cleanup).ConfigureAwait(false); }
        }
        observed.Should().BeSameAs(failure);
        lifetime.GetOwned<object>().Should().BeNull();
        Volatile.Read(ref cleanupCalls).Should().Be(1);
        scope.BackendStarts.Should().Be(0);
    }

    [Test]
    public async Task ACompletedCloseRejectsLateOwnedPublicationAndTheCallerRetiresTheUnacceptedOwnerAsync()
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        var lifetime = new ClientStartupLifetime();
        lifetime.SetMutationOwner(scope.Frontend.BackendMutations);
        await lifetime.CloseAsync(scope.Actions, static () => Task.CompletedTask).ConfigureAwait(false);
        var path = Path.Combine(scope.BackendPaths.ConfigDirectory, "late-host-resource.lock");
        var lateOwner = new OwnedFileStream(path);
        try
        {
            var published = false;
            lifetime.TryPublishOwned(lateOwner, _ => published = true).Should().BeFalse();
            published.Should().BeFalse();
            lifetime.GetOwned<OwnedFileStream>().Should().BeNull();
            lateOwner.Disposals.Should().Be(0, "the rejected result remains the producer's responsibility");
        }
        finally { await lateOwner.DisposeAsync().ConfigureAwait(false); }
        lateOwner.Disposals.Should().Be(1);
        using var reopened = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        reopened.CanWrite.Should().BeTrue();
    }

    private static void AssertPublicationAndFactoryAdmissionAreClosed(ClientStartupLifetime lifetime)
    {
        var published = false;
        Action publish = () => lifetime.Publish(() => published = true);
        publish.Should().Throw<ObjectDisposedException>();
        published.Should().BeFalse();
        var allocations = 0;
        Action create = () => _ = lifetime.CreateOwned(() => { allocations++; return new object(); });
        create.Should().Throw<ObjectDisposedException>();
        allocations.Should().Be(0);
    }

    private sealed class OwnedFileStream(string path) : FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref _disposals);
            base.Dispose(disposing);
        }
    }
}
