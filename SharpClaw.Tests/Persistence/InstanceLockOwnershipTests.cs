using Microsoft.Win32.SafeHandles;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection and executes its lock-ownership fault cases.")]
[TestFixture]
internal sealed class InstanceLockOwnershipTests
{
    private readonly List<string> _roots = [];

    [TearDown]
    public void TearDown()
    {
        for (var index = 0; index < _roots.Count; index++)
            Directory.Delete(_roots[index], recursive: true);
        _roots.Clear();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedMetadataClosesItsHandleAndReleasesTheExclusiveLease(bool failCleanup)
    {
        var root = CreateRoot();
        var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, root);
        paths.EnsureDirectories();
        var lockPath = Path.Combine(root, ".instance.lock");
        var metadataFailure = new IOException("Controlled instance-ownership metadata failure.");
        using var stream = new MetadataFailureStream(lockPath, metadataFailure, failCleanup);
        Action acquire = () =>
        {
            using var instanceLock = new SharpClawInstanceLock(paths, _ => stream);
        };

        var failure = acquire.Should().Throw<IOException>();

        failure.Which.Should().BeSameAs(metadataFailure,
            "a cleanup failure must preserve the metadata failure that prevented acquisition");
        stream.CapturedHandle.IsClosed.Should().BeTrue("metadata failure must close the acquired handle");
        Action acquireAgain = () =>
        {
            using var instanceLock = new SharpClawInstanceLock(paths);
            instanceLock.LockFilePath.Should().Be(lockPath);
        };
        acquireAgain.Should().NotThrow("the failed acquisition must release its exclusive path lease");
    }

    private string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpclaw-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private sealed class MetadataFailureStream : FileStream
    {
        private readonly IOException _metadataFailure;
        private readonly bool _failCleanup;
        private int _disposed;

        public MetadataFailureStream(string path, IOException metadataFailure, bool failCleanup)
            : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        {
            _metadataFailure = metadataFailure;
            _failCleanup = failCleanup;
            CapturedHandle = SafeFileHandle;
        }

        public SafeFileHandle CapturedHandle { get; }

        public override void SetLength(long value) => throw _metadataFailure;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && _failCleanup)
                throw new IOException("Controlled instance-lock cleanup failure.");
        }
    }
}
