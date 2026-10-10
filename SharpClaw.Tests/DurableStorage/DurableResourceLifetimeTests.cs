using Microsoft.Win32.SafeHandles;
using SharpClaw.Shared.DurableStorage;

namespace SharpClaw.Tests.DurableStorage;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection and executes its durable-resource fault cases.")]
[TestFixture]
internal sealed class DurableResourceLifetimeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private readonly List<string> _roots = [];

    [TearDown]
    public void TearDown()
    {
        for (var index = 0; index < _roots.Count; index++)
            Directory.Delete(_roots[index], recursive: true);
        _roots.Clear();
    }

    [Test]
    public async Task FailedSealClosesEverySegmentAndReleasesTheWriterLeaseAsync()
    {
        var root = CreateRoot();
        var opener = new ControlledSegmentOpener();
        using var cancellation = new CancellationTokenSource(TestTimeout);
#pragma warning disable CA2000 // Every path joins disposal in finally and releases the captured streams. This faulted disposal reports the injected seal failure after cleanup; await using would rethrow that asserted failure.
        var store = new DurableSegmentStore(new() { RootDirectory = root }, opener.CreateStream);
#pragma warning restore CA2000
        try
        {
            await store.AppendAsync(DurableStreamKey.Job(Guid.NewGuid()), Record("failed seal"),
                DurableWriteMode.Buffered, cancellation.Token).AsTask()
                .WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
            await store.AppendAsync(DurableStreamKey.Job(Guid.NewGuid()), Record("other stream"),
                DurableWriteMode.Buffered, cancellation.Token).AsTask()
                .WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
            Func<Task> dispose = () => store.DisposeAsync().AsTask()
                .WaitAsync(TestTimeout, CancellationToken.None);

            var failure = await dispose.Should().ThrowAsync<IOException>().ConfigureAwait(false);

            failure.Which.Should().BeSameAs(opener.SealFailure);
            opener.Streams.Should().HaveCount(2);
            for (var index = 0; index < opener.Streams.Count; index++)
                opener.Streams[index].CapturedHandle.IsClosed.Should().BeTrue(
                    "one seal failure cannot strand another stream or the failed stream's handle");
            AssertWriterLeaseReleased(root);
            var repeatedFailure = await dispose.Should().ThrowAsync<IOException>().ConfigureAwait(false);
            repeatedFailure.Which.Should().BeSameAs(opener.SealFailure);
        }
        finally
        {
            try
            {
                await store.DisposeAsync().AsTask().WaitAsync(TestTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (IOException exception) when (ReferenceEquals(exception, opener.SealFailure))
            {
                // The test already asserted this retained disposal failure.
            }
            finally
            {
                await opener.ReleaseStreamsAsync().WaitAsync(TestTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task FailedOpenRecoveryReleasesItsHandleBeforeStoreDisposalAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var writer = new DurableSegmentStore(new() { RootDirectory = root });
        try
        {
            await writer.AppendAsync(key, Record("crash tail"),
                cancellationToken: cancellation.Token).AsTask()
                .WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().AsTask().WaitAsync(TestTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }

        var sealedPath = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Should().ContainSingle().Which;
        var openPath = Path.ChangeExtension(sealedPath, ".open");
        File.Move(sealedPath, openPath);
        var bytes = await File.ReadAllBytesAsync(openPath, cancellation.Token).ConfigureAwait(false);
        bytes[0] ^= 0xff;
        await File.WriteAllBytesAsync(openPath, bytes, cancellation.Token).ConfigureAwait(false);
        var recovered = new DurableSegmentStore(new() { RootDirectory = root });
        try
        {
            Func<Task> recover = () => recovered.GetSummaryAsync(key, cancellation.Token).AsTask()
                .WaitAsync(TestTimeout, CancellationToken.None);

            await recover.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(false);

            Action acquireSegment = () =>
            {
                using var stream = new FileStream(openPath, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None);
            };
            acquireSegment.Should().NotThrow("failed recovery must close its unpublished segment handle");
        }
        finally
        {
            await recovered.DisposeAsync().AsTask().WaitAsync(TestTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpclaw-durable-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static void AssertWriterLeaseReleased(string root)
    {
        using var lease = new FileStream(Path.Combine(root, ".writer.lease"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static DurableRecordWrite Record(string message) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Information", "lifetime-test", message);

    private sealed class ControlledSegmentOpener
    {
        private readonly List<ControlledSealStream> _streams = [];

        public IOException SealFailure { get; } = new("Controlled segment-seal failure.");
        public List<ControlledSealStream> Streams => _streams;

        public ControlledSealStream CreateStream(string path)
        {
            var stream = new ControlledSealStream(path, _streams.Count == 0 ? SealFailure : null);
            _streams.Add(stream);
            return stream;
        }

        public async Task ReleaseStreamsAsync()
        {
            for (var index = 0; index < _streams.Count; index++)
            {
                var stream = _streams[index];
                stream.AllowFlush();
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ControlledSealStream : FileStream
    {
        private IOException? _flushFailure;

        public ControlledSealStream(string path, IOException? flushFailure)
            : base(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.Asynchronous)
        {
            _flushFailure = flushFailure;
            CapturedHandle = SafeFileHandle;
        }

        public SafeFileHandle CapturedHandle { get; }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _flushFailure is { } failure
                ? Task.FromException(failure)
                : base.FlushAsync(cancellationToken);

        public void AllowFlush() => _flushFailure = null;
    }
}
