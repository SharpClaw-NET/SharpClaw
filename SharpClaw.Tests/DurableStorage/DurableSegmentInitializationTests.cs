using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using SharpClaw.Shared.DurableStorage;

namespace SharpClaw.Tests.DurableStorage;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection and executes its fault-injection cases.")]
[TestFixture]
internal sealed class DurableSegmentInitializationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private readonly List<string> _roots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (ref readonly var root in CollectionsMarshal.AsSpan(_roots))
            Directory.Delete(root, recursive: true);
        _roots.Clear();
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task FailedHeaderClosesItsHandleAndAllowsAnotherAppendAsync(
        bool cancelHeader,
        bool failCleanup)
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpclaw-header-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var opener = new ControlledSegmentOpener(failCleanup);
        using var cancellation = new CancellationTokenSource();
        var store = new DurableSegmentStore(new() { RootDirectory = root }, opener.CreateStream);
        await using var storeDisposal = store.ConfigureAwait(false);
        var key = DurableStreamKey.Job(Guid.NewGuid());
        var append = store.AppendAsync(key, Record("failed header"),
            cancellationToken: cancellation.Token).AsTask();
        try
        {
            var stream = await opener.Opened.Task.WaitAsync(TestTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            await stream.HeaderWriteEntered.Task.WaitAsync(TestTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            if (cancelHeader)
                await cancellation.CancelAsync().ConfigureAwait(false);
            else
                opener.AllowHeaderWrite();

            await AssertFailureAsync(append, opener, cancelHeader, cancellation.Token)
                .ConfigureAwait(false);
            stream.CapturedHandle.IsClosed.Should().BeTrue("the store acquired the failed segment handle");
            var receipt = await store.AppendAsync(key, Record("retry"),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            receipt.Sequence.Should().Be(1, "the failed header cannot consume a record sequence");
            receipt.RecordCount.Should().Be(1);
        }
        finally
        {
            await SettleAppendAsync(append, opener, cancellation).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DisposalDrainsAdmittedAppendBeforeReleasingItsWriterLeaseAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpclaw-drain-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var opener = new ControlledSegmentOpener(failCleanup: false, failHeader: false);
        using var cancellation = new CancellationTokenSource();
        var store = new DurableSegmentStore(new() { RootDirectory = root }, opener.CreateStream);
        await using var storeDisposal = store.ConfigureAwait(false);
        var key = DurableStreamKey.Job(Guid.NewGuid());
        var append = store.AppendAsync(key, Record("admitted"), DurableWriteMode.Buffered,
            cancellation.Token).AsTask();
        try
        {
            var stream = await opener.Opened.Task.WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
            await stream.HeaderWriteEntered.Task.WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
            var first = store.DisposeAsync().AsTask();
            var second = store.DisposeAsync().AsTask();
            second.Should().BeSameAs(first);
            first.IsCompleted.Should().BeFalse("the store owns the admitted append until its header settles");
            stream.CapturedHandle.IsClosed.Should().BeFalse();
            Action acquireLease = () =>
            {
                using var lease = new FileStream(Path.Combine(root, ".writer.lease"),
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            };
            acquireLease.Should().Throw<IOException>("the admitted append still owns its writer lease");

            opener.AllowHeaderWrite();
            var receipt = await append.WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
            receipt.Sequence.Should().Be(1);
            await Task.WhenAll(first, second).WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
            stream.CapturedHandle.IsClosed.Should().BeTrue();
            acquireLease.Should().NotThrow("completed disposal releases the exclusive writer lease");
            Func<Task> appendAfterDisposal = () => store.AppendAsync(key, Record("closed"),
                cancellationToken: CancellationToken.None).AsTask();
            await appendAfterDisposal.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false);
        }
        finally
        {
            await SettleAppendAsync(append, opener, cancellation).ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003",
        Justification = "The fixture starts this append before opening its controlled gate. This bounded context-free join observes that exact owned operation; no JoinableTaskFactory is involved.")]
    private static async Task AssertFailureAsync(
        Task<DurableAppendReceipt> append,
        ControlledSegmentOpener opener,
        bool cancelled,
        CancellationToken cancellationToken)
    {
        Func<Task> operation = async () =>
        {
            await append.WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
        };
        if (cancelled)
        {
            var failure = await operation.Should().ThrowAsync<OperationCanceledException>()
                .ConfigureAwait(false);
            failure.Which.CancellationToken.Should().Be(cancellationToken);
        }
        else if (opener.FailCleanup)
        {
            var failure = await operation.Should().ThrowAsync<AggregateException>()
                .ConfigureAwait(false);
            failure.Which.InnerExceptions.Should().Equal(opener.HeaderFailure, opener.CleanupFailure);
        }
        else
        {
            var failure = await operation.Should().ThrowAsync<IOException>().ConfigureAwait(false);
            failure.Which.Should().BeSameAs(opener.HeaderFailure);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003",
        Justification = "Cleanup cancels and releases the fixture-owned append, joins it with a bounded wait, then observes its already-completed handle receipt. All continuations run without a UI context.")]
    private static async Task SettleAppendAsync(
        Task<DurableAppendReceipt> append,
        ControlledSegmentOpener opener,
        CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        opener.AllowHeaderWrite();
        try
        {
            await append.WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException exception) when (ReferenceEquals(exception, opener.HeaderFailure))
        {
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.Contains(opener.HeaderFailure)
            && exception.InnerExceptions.Contains(opener.CleanupFailure))
        {
        }

        if (!opener.Opened.Task.IsCompletedSuccessfully)
            return;
        var stream = await opener.Opened.Task.ConfigureAwait(false);
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException exception) when (ReferenceEquals(exception, opener.CleanupFailure))
        {
            // A failing implementation must still leave the test's acquired handle closed.
        }
    }

    private static DurableRecordWrite Record(string message) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Information", "header-test", message);

    private sealed class ControlledSegmentOpener(bool failCleanup, bool failHeader = true)
    {
        private int _openedCount;
        private readonly TaskCompletionSource _writePermission = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailCleanup { get; } = failCleanup;
        public bool FailHeader { get; } = failHeader;
        public IOException HeaderFailure { get; } = new("Controlled initial-header failure.");
        public IOException CleanupFailure { get; } = new("Controlled handle-cleanup failure.");
        public TaskCompletionSource<ControlledHeaderStream> Opened { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public FileStream CreateStream(string path)
        {
            if (Interlocked.Increment(ref _openedCount) > 1)
                return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.Read, 4096, FileOptions.Asynchronous);

            var stream = new ControlledHeaderStream(path, this, _writePermission.Task);
            Opened.TrySetResult(stream);
            return stream;
        }

        public void AllowHeaderWrite() => _writePermission.TrySetResult();
    }

    private sealed class ControlledHeaderStream : FileStream
    {
        private readonly ControlledSegmentOpener _opener;
        private readonly Task _writePermission;
        private int _disposed;

        public ControlledHeaderStream(string path, ControlledSegmentOpener opener, Task writePermission)
            : base(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096,
                FileOptions.Asynchronous)
        {
            _opener = opener;
            _writePermission = writePermission;
            CapturedHandle = SafeFileHandle;
        }

        public SafeFileHandle CapturedHandle { get; }
        public TaskCompletionSource HeaderWriteEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            HeaderWriteEntered.TrySetResult();
            await _writePermission.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_opener.FailHeader)
                throw _opener.HeaderFailure;
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _opener.FailCleanup)
                throw _opener.CleanupFailure;
        }
    }
}
