using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Shared.DurableStorage;

namespace SharpClaw.Tests.DurableStorage;

[TestFixture]
public sealed class DurableSegmentStoreTests
{
    private static readonly byte[] TestEncryptionKey =
        SHA256.HashData("SharpClaw durable segment tests"u8);
    private readonly List<string> _roots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _roots)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        _roots.Clear();
    }

    [Test]
    public async Task ReadAsync_EnforcesRecordAndByteCaps()
    {
        var root = CreateRoot();
        await using var store = CreateStore(root);
        var key = DurableStreamKey.Job(Guid.NewGuid());

        for (var index = 0; index < 8; index++)
            await store.AppendAsync(key, Record($"message-{index}-{new string('x', 80)}"));

        var page = await store.ReadAsync(
            key,
            1,
            new DurableReadOptions(
                Take: 3,
                MaxBytes: 900,
                MaxScanBytes: 4096));

        page.Records.Should().NotBeEmpty();
        page.Records.Count.Should().BeLessThanOrEqualTo(3);
        page.ReturnedBytes.Should().BeLessThanOrEqualTo(900);
        page.HasMore.Should().BeTrue();
        page.NextSequence.Should().NotBeNull();
    }

    [Test]
    public async Task ReadAsync_RejectsCallerScanBudgetsAboveTheStoreCeiling()
    {
        var root = CreateRoot();
        await using var store = CreateStore(root);
        var key = DurableStreamKey.Job(Guid.NewGuid());
        await store.AppendAsync(key, Record("bounded"));

        Func<Task> read = async () =>
            _ = await store.ReadAsync(
                key,
                1,
                new DurableReadOptions(
                    MaxBytes: 1024,
                    MaxScanBytes: 16L * 1024 * 1024 + 1));

        await read.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("MaxScanBytes");
    }

    [Test]
    public async Task SealAsync_EvictsIdleStateAndReadsDoNotRetainIt()
    {
        var root = CreateRoot();
        await using var store = CreateStore(root);
        var key = DurableStreamKey.Job(Guid.NewGuid());

        await store.AppendAsync(key, Record("terminal"));
        store.GetSnapshot().ResidentStreams.Should().Be(1);

        await store.SealAsync(key);
        store.GetSnapshot().ResidentStreams.Should().Be(0);

        var page = await store.ReadAsync(
            key,
            1,
            new DurableReadOptions(MaxScanBytes: 1024 * 1024));
        page.Records.Should().ContainSingle();
        store.GetSnapshot().ResidentStreams.Should().Be(0);
    }

    [Test]
    public void Constructor_RejectsSegmentsLargerThanTheReadScanCeiling()
    {
        var root = CreateRoot();

        var create = () => new DurableSegmentStore(new DurableStorageOptions
        {
            RootDirectory = root,
            EncryptionKey = TestEncryptionKey,
            SegmentMaxBytes = 2 * 1024 * 1024,
            MaxRecordBytes = 16 * 1024,
            MaxPageBytes = 1024 * 1024,
            MaxReadScanBytes = 1024 * 1024,
            AcquireWriterLease = false,
        });

        create.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("SegmentMaxBytes");
    }

    [Test]
    public async Task ReadAsync_EvaluatesTheRecordThatCrossesTheScanBudget()
    {
        var root = CreateRoot();
        await using var store = CreateStore(root);
        var key = DurableStreamKey.TaskLog(Guid.NewGuid());

        for (var index = 0; index < 5; index++)
            await store.AppendAsync(key, Record(RandomMessage("skip")));
        var matching = Record(RandomMessage("needle"));
        await store.AppendAsync(key, matching);
        await store.AppendAsync(key, Record(RandomMessage("tail")));
        await store.FlushAsync(key);

        var openPath = Directory.GetFiles(root, "*.open", SearchOption.AllDirectories)
            .Single();
        var frameBytes = ReadFrameEncodedBytes(openPath);
        var scanBudget = frameBytes.Take(5).Sum() + 1L;
        var matchingJsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            new DurableRecord(
                6,
                matching.RecordId,
                matching.Timestamp,
                matching.Level,
                matching.EventName,
                matching.Message,
                matching.ExceptionType,
                matching.CorrelationId,
                matching.Artifact)).Length;
        scanBudget.Should().BeLessThan(frameBytes.Take(6).Sum());

        var page = await store.ReadAsync(
            key,
            1,
            new DurableReadOptions(
                Take: 10,
                MaxBytes: matchingJsonBytes + 32,
                Contains: "needle",
                MaxScanBytes: scanBudget));

        page.Records.Should().ContainSingle();
        page.Records[0].RecordId.Should().Be(matching.RecordId);
        page.NextSequence.Should().Be(7);
        page.HasMore.Should().BeTrue();
    }

    [Test]
    public async Task Reopen_RecoversAFlushedFooterLeftBeforeRename()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        await using (var first = CreateStore(root))
        {
            await first.AppendAsync(key, Record("before-crash"));
            await first.SealAsync(key);
        }

        var sealedPath = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Single();
        var openPath = Path.ChangeExtension(sealedPath, ".open");
        File.Move(sealedPath, openPath);

        await using (var recovered = CreateStore(root))
        {
            var receipt = await recovered.AppendAsync(key, Record("after-crash"));
            receipt.Sequence.Should().Be(2);
            var page = await recovered.ReadAsync(
                key,
                1,
                new DurableReadOptions(MaxScanBytes: 1024 * 1024));
            page.Records.Select(record => record.Message)
                .Should().Equal("before-crash", "after-crash");
        }
    }

    [Test]
    public async Task IdempotentAppend_SurvivesRestartWithoutDuplicatingTheRecord()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.TaskOutput(Guid.NewGuid());
        var record = Record("exactly-once") with { Idempotent = true };

        await using (var first = CreateStore(root))
        {
            var receipt = await first.AppendAsync(key, record);
            receipt.Sequence.Should().Be(1);
        }

        await using (var second = CreateStore(root))
        {
            var receipt = await second.AppendAsync(key, record);
            receipt.Sequence.Should().Be(1);
            var page = await second.ReadAsync(
                key,
                1,
                new DurableReadOptions(MaxScanBytes: 1024 * 1024));
            page.Records.Should().ContainSingle();
        }
    }

    [Test]
    public async Task BufferedIdempotentAppend_SealRebuildsADeletedDerivedIndex()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        var record = Record("terminal") with { Idempotent = true };

        await using (var first = CreateStore(root))
        {
            await first.AppendAsync(
                key,
                record,
                DurableWriteMode.Buffered);
            await first.SealAsync(key);
        }

        var index = Directory.GetFiles(
                root,
                ".idempotency",
                SearchOption.AllDirectories)
            .Single();
        File.Delete(index);

        await using var recovered = CreateStore(root);
        var receipt = await recovered.AppendAsync(key, record);
        receipt.Sequence.Should().Be(1);
        var page = await recovered.ReadAsync(
            key,
            1,
            new DurableReadOptions(MaxScanBytes: 1024 * 1024));
        page.Records.Should().ContainSingle();
    }

    [Test]
    public async Task ReadAsync_RejectsWrongKeysAndSealedSegmentCorruption()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Process("runtime", Guid.NewGuid());
        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        await using (var writer = CreateStore(root, encryptionKey))
        {
            await writer.AppendAsync(key, Record("protected"));
        }

        await using (var wrongKeyStore = CreateStore(
                         root,
                         RandomNumberGenerator.GetBytes(32)))
        {
            Func<Task> readWithWrongKey = async () =>
                _ = await wrongKeyStore.ReadAsync(
                    key,
                    1,
                    new DurableReadOptions(MaxScanBytes: 1024 * 1024));
            await readWithWrongKey.Should().ThrowAsync<CryptographicException>();
        }

        var segment = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Single();
        var bytes = await File.ReadAllBytesAsync(segment);
        bytes[48] ^= 0x40;
        await File.WriteAllBytesAsync(segment, bytes);

        await using var corruptStore = CreateStore(root, encryptionKey);
        Func<Task> readCorrupt = async () =>
            _ = await corruptStore.ReadAsync(
                key,
                1,
                new DurableReadOptions(MaxScanBytes: 1024 * 1024));
        await readCorrupt.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task OperationalCatalogEnumeratesTypedBootsAcrossRestartWithoutReadingBodies()
    {
        var root = CreateRoot();
        var processBoot = Guid.Empty;
        var moduleBoot = Guid.NewGuid();
        var processKey = DurableStreamKey.Process("Runtime/Host", processBoot);
        var moduleKey = DurableStreamKey.Module("Module/One", moduleBoot);
        try
        {
            await using (var store = CreateStore(root))
            {
                await store.AppendAsync(processKey, Record("process"));
                await store.SealAsync(processKey);
                await store.AppendAsync(moduleKey, Record("module"));

                var processDirectory = new DurableStreamPathEncoder(root)
                    .GetStreamDirectory(processKey);
                var processSegment = Directory.GetFiles(
                        processDirectory,
                        "*.scseg")
                    .Should().ContainSingle().Subject;
                using (var corrupt = new FileStream(
                           processSegment,
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.ReadWrite))
                using (var reader = new BinaryReader(
                           corrupt,
                           System.Text.Encoding.UTF8,
                           leaveOpen: true))
                {
                    corrupt.Position = 40;
                    var frameLength = reader.ReadInt32();
                    frameLength.Should().BeGreaterThan(0);
                    var payloadPosition = corrupt.Position;
                    var payload = corrupt.ReadByte();
                    payload.Should().BeGreaterThanOrEqualTo(0);
                    corrupt.Position = payloadPosition;
                    corrupt.WriteByte((byte)(payload ^ 0xFF));
                    corrupt.Flush(flushToDisk: true);
                }

                var catalog = await store.EnumerateOperationalStreamsAsync(
                    new DurableOperationalStreamEnumerationOptions
                    {
                        MaxEntries = 10,
                        MaxScanBytes = 1024 * 1024,
                        MaxDuration = TimeSpan.FromSeconds(2),
                    });

                catalog.IdentityGaps.Should().BeEmpty();
                catalog.Streams.Should().HaveCount(2);
                var process = catalog.Streams.Single(summary =>
                    summary.Stream.Kind == DurableStreamKind.ProcessLog);
                process.AppName.Should().Be("runtime/host");
                process.ModuleId.Should().BeNull();
                process.BootId.Should().Be(Guid.Empty);
                process.HasActiveSegment.Should().BeFalse();
                process.HasSealedSegments.Should().BeTrue();
                process.RecordCount.Should().Be(1);
                process.FirstAvailableSequence.Should().Be(1);

                var module = catalog.Streams.Single(summary =>
                    summary.Stream.Kind == DurableStreamKind.ModuleLog);
                module.AppName.Should().BeNull();
                module.ModuleId.Should().Be("module/one");
                module.BootId.Should().Be(moduleBoot);
                module.HasActiveSegment.Should().BeTrue();
                module.HasSealedSegments.Should().BeFalse();
                module.RecordCount.Should().Be(1);
            }

            await using var restarted = CreateStore(root);
            var afterRestart = await restarted.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 10,
                    MaxScanBytes = 1024 * 1024,
                    MaxDuration = TimeSpan.FromSeconds(2),
                });
            afterRestart.IdentityGaps.Should().BeEmpty();
            afterRestart.Streams.Select(summary => summary.Stream)
                .Should().Contain(processKey);
            afterRestart.Streams.Select(summary => summary.Stream)
                .Should().Contain(moduleKey);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Test]
    public async Task OperationalCatalogSkipsMalformedMetadataAndKeepsValidBoots()
    {
        var root = CreateRoot();
        var invalidKey = DurableStreamKey.Process("invalid", Guid.NewGuid());
        var validKey = DurableStreamKey.Process("valid", Guid.NewGuid());
        try
        {
            await using (var store = CreateStore(root))
            {
                await store.AppendAsync(invalidKey, Record("invalid"));
                await store.SealAsync(invalidKey);
                await store.AppendAsync(validKey, Record("valid"));
                await store.SealAsync(validKey);
            }

            var invalidDirectory = new DurableStreamPathEncoder(root)
                .GetStreamDirectory(invalidKey);
            File.WriteAllText(
                Path.Combine(invalidDirectory, ".stream.manifest"),
                "{");

            await using var reader = CreateStore(root);
            var catalog = await reader.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 10,
                    MaxScanBytes = 1024 * 1024,
                    MaxDuration = TimeSpan.FromSeconds(2),
                });

            catalog.Streams.Select(summary => summary.Stream)
                .Should().Contain(validKey);
            catalog.Streams.Select(summary => summary.Stream)
                .Should().NotContain(invalidKey);
            catalog.IdentityGaps.Should().ContainSingle(gap =>
                gap.Reason == "InvalidSegmentMetadata");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Test]
    public async Task OperationalCatalogReportsLegacyIdentityGapInsteadOfScanningBodies()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Process("legacy", Guid.NewGuid());
        try
        {
            await using (var store = CreateStore(root))
            {
                await store.AppendAsync(
                    key,
                    Record("legacy body"));
                await store.SealAsync(key);
            }

            var directory = new DurableStreamPathEncoder(root)
                .GetStreamDirectory(key);
            File.Delete(Path.Combine(directory, ".stream.identity"));

            await using var reader = CreateStore(root);
            var catalog = await reader.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 10,
                    MaxScanBytes = 1024 * 1024,
                    MaxDuration = TimeSpan.FromSeconds(2),
                });

            catalog.Streams.Should().BeEmpty();
            catalog.IdentityGaps.Should().ContainSingle();
            catalog.IdentityGaps[0].Kind.Should().Be(DurableStreamKind.ProcessLog);
            catalog.IdentityGaps[0].Reason
                .Should().Be("IdentityUnavailableWithoutBodyScan");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Test]
    public async Task OperationalCatalogBoundsSummariesAndMetadataScan()
    {
        var root = CreateRoot();
        try
        {
            await using (var store = CreateStore(root))
            {
                foreach (var app in new[] { "first", "second" })
                {
                    var key = DurableStreamKey.Process(app, Guid.NewGuid());
                    await store.AppendAsync(key, Record(app));
                    await store.SealAsync(key);
                }
            }

            await using var reader = CreateStore(root);
            var entryBound = await reader.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 1,
                    MaxScanBytes = 1024 * 1024,
                    MaxDuration = TimeSpan.FromSeconds(2),
                });
            (entryBound.Streams.Count + entryBound.IdentityGaps.Count)
                .Should().BeLessThanOrEqualTo(1);
            entryBound.HasMore.Should().BeTrue();

            var scanBound = await reader.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 10,
                    MaxScanBytes = 1,
                    MaxDuration = TimeSpan.FromSeconds(2),
                });
            scanBound.Streams.Should().BeEmpty();
            scanBound.HasMore.Should().BeTrue();
            scanBound.ScannedBytes.Should().BeLessThanOrEqualTo(1);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Test]
    public void OperationalKeyParserMatchesWritableKeySemantics()
    {
        var bootId = Guid.Empty;
        var key = DurableStreamKey.Module("Module/With/Slashes", bootId);

        DurableStreamKey.TryParseOperational(
                key.CanonicalValue,
                out var parsed,
                out var appName,
                out var moduleId,
                out var parsedBootId)
            .Should().BeTrue();
        parsed.Should().Be(key);
        appName.Should().BeNull();
        moduleId.Should().Be("module/with/slashes");
        parsedBootId.Should().Be(Guid.Empty);
    }

    [Test]
    public async Task Retention_DeletesOnlyASealedPrefixAndPersistsExpiryWatermarks()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        await using var store = CreateStore(root);
        for (var index = 1; index <= 3; index++)
        {
            await store.AppendAsync(key, Record($"record-{index}"));
            await store.SealAsync(key);
        }

        var segments = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        File.SetLastWriteTimeUtc(segments[0], DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(segments[1], DateTime.UtcNow.AddDays(-10));

        var result = await store.ApplyRetentionAsync(new DurableRetentionOptions
        {
            JobLogAge = TimeSpan.FromDays(1),
            TaskLogAge = TimeSpan.FromDays(30),
            TaskOutputAge = TimeSpan.FromDays(30),
            ProcessLogAge = TimeSpan.FromDays(30),
            ModuleLogAge = TimeSpan.FromDays(30),
            MaximumEncodedBytes = long.MaxValue,
            MinimumFreeBytes = 0,
        });

        result.DeletedSegments.Should().Be(2);
        var summary = await store.GetSummaryAsync(key);
        summary.FirstAvailableSequence.Should().Be(3);
        summary.ExpiredRecordCount.Should().Be(2);
        var page = await store.ReadAsync(
            key,
            1,
            new DurableReadOptions(MaxScanBytes: 1024 * 1024));
        page.Records.Select(record => record.Sequence).Should().Equal(3);
        page.FirstAvailableSequence.Should().Be(3);
        page.ExpiredRecordCount.Should().Be(2);
    }

    [Test]
    public async Task ArtifactReferenceIndex_TracksRetainedRecordsAndPrunesExpiredPrefixes()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.TaskOutput(Guid.NewGuid());
        var artifactId = Guid.NewGuid();
        await using var store = CreateStore(root);
        await store.AppendAsync(
            key,
            Record("externalized") with
            {
                Artifact = new DurableArtifactReference(
                    artifactId,
                    "text/plain",
                    12,
                    new string('a', 64)),
            });
        await store.SealAsync(key);

        (await store.ReadArtifactReferencesAsync()).Should().Contain(artifactId);
        var segment = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Single();
        File.SetLastWriteTimeUtc(segment, DateTime.UtcNow.AddDays(-10));

        await store.ApplyRetentionAsync(new DurableRetentionOptions
        {
            JobLogAge = TimeSpan.FromDays(30),
            TaskLogAge = TimeSpan.FromDays(30),
            TaskOutputAge = TimeSpan.FromDays(1),
            ProcessLogAge = TimeSpan.FromDays(30),
            ModuleLogAge = TimeSpan.FromDays(30),
            MaximumEncodedBytes = long.MaxValue,
            MinimumFreeBytes = 0,
        });

        (await store.ReadArtifactReferencesAsync()).Should().NotContain(artifactId);
    }

    [Test]
    public async Task Retention_RecoversAndExpiresAnUntrackedCrashOpenSegment()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        await using (var writer = CreateStore(root))
            await writer.AppendAsync(key, Record("crash tail"));
        var sealedPath = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Single();
        var openPath = Path.ChangeExtension(sealedPath, ".open");
        File.Move(sealedPath, openPath);
        File.SetLastWriteTimeUtc(openPath, DateTime.UtcNow.AddDays(-10));

        await using var recovered = CreateStore(root);
        var result = await recovered.ApplyRetentionAsync(new DurableRetentionOptions
        {
            JobLogAge = TimeSpan.FromDays(1),
            TaskLogAge = TimeSpan.FromDays(30),
            TaskOutputAge = TimeSpan.FromDays(30),
            ProcessLogAge = TimeSpan.FromDays(30),
            ModuleLogAge = TimeSpan.FromDays(30),
            MaximumEncodedBytes = long.MaxValue,
            MinimumFreeBytes = 0,
        });

        result.DeletedSegments.Should().Be(1);
        Directory.GetFiles(root, "*.open", SearchOption.AllDirectories)
            .Should().BeEmpty();
        var summary = await recovered.GetSummaryAsync(key);
        summary.ExpiredRecordCount.Should().Be(1);
    }

    private string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            "durable-segments",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        _roots.Remove(root);
    }

    private static DurableSegmentStore CreateStore(
        string root,
        byte[]? encryptionKey = null) =>
        new(new DurableStorageOptions
        {
            RootDirectory = root,
            EncryptionKey = encryptionKey ?? TestEncryptionKey,
            SegmentMaxBytes = 64 * 1024,
            SegmentMaxAge = TimeSpan.FromHours(1),
            MaxRecordBytes = 16 * 1024,
            MaxPageRecords = 1000,
            MaxPageBytes = 1024 * 1024,
            AcquireWriterLease = false,
        });

    private static DurableRecordWrite Record(string message) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "Information",
            "test.record",
            message);

    private static string RandomMessage(string prefix) =>
        prefix + "-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(120));

    private static IReadOnlyList<int> ReadFrameEncodedBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        stream.Position = 40;
        var lengths = new List<int>();
        using var reader = new BinaryReader(stream);
        while (stream.Position < stream.Length)
        {
            var frameLength = reader.ReadInt32();
            if (frameLength == -1)
                break;
            lengths.Add(sizeof(int) + frameLength);
            stream.Position += frameLength;
        }
        return lengths;
    }
}
