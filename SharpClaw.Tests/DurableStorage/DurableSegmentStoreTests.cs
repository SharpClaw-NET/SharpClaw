using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SharpClaw.Shared.DurableStorage;

namespace SharpClaw.Tests.DurableStorage;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class DurableSegmentStoreTests
{
    private static readonly byte[] TestEncryptionKey =
        SHA256.HashData("SharpClaw durable segment tests"u8);
    private readonly List<string> _roots = [];

    [TearDown]
    public void TearDown()
    {
        for (var index = 0; index < _roots.Count; index++)
        {
            var root = _roots[index];
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        _roots.Clear();
    }

    [Test]
    public async Task ReadAsync_EnforcesRecordAndByteCapsAsync()
    {
        var root = CreateRoot();
        var store = CreateStore(root);
        await using var storeAsyncDisposal = store.ConfigureAwait(false);
        var key = DurableStreamKey.Job(Guid.NewGuid());

        for (var index = 0; index < 8; index++)
            await store.AppendAsync(key, Record($"message-{index}-{new string('x', 80)}")).ConfigureAwait(false);

        var page = await store.ReadAsync(
            key,
            1,
            new DurableReadOptions(
                Take: 3,
                MaxBytes: 900,
                MaxScanBytes: 4096)).ConfigureAwait(false);

        page.Records.Should().NotBeEmpty();
        page.Records.Count.Should().BeLessThanOrEqualTo(3);
        page.ReturnedBytes.Should().BeLessThanOrEqualTo(900);
        page.HasMore.Should().BeTrue();
        page.NextSequence.Should().NotBeNull();
    }

    [Test]
    public async Task ReadAsync_RejectsCallerScanBudgetsAboveTheStoreCeilingAsync()
    {
        var root = CreateRoot();
        var store = CreateStore(root);
        await using var storeAsyncDisposal_ = store.ConfigureAwait(false);
        var key = DurableStreamKey.Job(Guid.NewGuid());
        await store.AppendAsync(key, Record("bounded")).ConfigureAwait(false);

        Func<Task> read = async () =>
            _ = await store.ReadAsync(
                key,
                1,
                new DurableReadOptions(
                    MaxBytes: 1024,
                    MaxScanBytes: 16L * 1024 * 1024 + 1)).ConfigureAwait(false);

        await read.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("MaxScanBytes").ConfigureAwait(false);
    }

    [Test]
    public async Task SealAsync_EvictsIdleStateAndReadsDoNotRetainItAsync()
    {
        var root = CreateRoot();
        var store = CreateStore(root);
        await using var storeAsyncDisposal__ = store.ConfigureAwait(false);
        var key = DurableStreamKey.Job(Guid.NewGuid());

        await store.AppendAsync(key, Record("terminal")).ConfigureAwait(false);
        store.GetSnapshot().ResidentStreams.Should().Be(1);

        await store.SealAsync(key).ConfigureAwait(false);
        store.GetSnapshot().ResidentStreams.Should().Be(0);

        var page = await store.ReadAsync(
            key,
            1,
            new DurableReadOptions(MaxScanBytes: 1024 * 1024)).ConfigureAwait(false);
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
    public async Task ReadAsync_EvaluatesTheRecordThatCrossesTheScanBudgetAsync()
    {
        var root = CreateRoot();
        var store = CreateStore(root);
        await using var storeAsyncDisposal___ = store.ConfigureAwait(false);
        var key = DurableStreamKey.Job(Guid.NewGuid());

        for (var index = 0; index < 5; index++)
            await store.AppendAsync(key, Record(RandomMessage("skip"))).ConfigureAwait(false);
        var matching = Record(RandomMessage("needle"));
        await store.AppendAsync(key, matching).ConfigureAwait(false);
        await store.AppendAsync(key, Record(RandomMessage("tail"))).ConfigureAwait(false);
        await store.FlushAsync(key).ConfigureAwait(false);

        var openPath = Directory.GetFiles(root, "*.open", SearchOption.AllDirectories)
            .Should().ContainSingle().Which;
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
                MaxScanBytes: scanBudget)).ConfigureAwait(false);

        page.Records.Should().ContainSingle();
        page.Records[0].RecordId.Should().Be(matching.RecordId);
        page.NextSequence.Should().Be(7);
        page.HasMore.Should().BeTrue();
    }

    [Test]
    public async Task Reopen_RecoversAFlushedFooterLeftBeforeRenameAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        {
            var first = CreateStore(root);
            await using (first.ConfigureAwait(false))
            {
                await first.AppendAsync(key, Record("before-crash")).ConfigureAwait(false);
                await first.SealAsync(key).ConfigureAwait(false);
            }
        }

        var sealedPath = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Should().ContainSingle().Which;
        var openPath = Path.ChangeExtension(sealedPath, ".open");
        File.Move(sealedPath, openPath);

        {
            var recovered = CreateStore(root);
            await using (recovered.ConfigureAwait(false))
            {
                var receipt = await recovered.AppendAsync(key, Record("after-crash")).ConfigureAwait(false);
                receipt.Sequence.Should().Be(2);
                var page = await recovered.ReadAsync(
                    key,
                    1,
                    new DurableReadOptions(MaxScanBytes: 1024 * 1024)).ConfigureAwait(false);
                page.Records.Select(record => record.Message)
                    .Should().Equal("before-crash", "after-crash");
            }
        }
    }

    [Test]
    public async Task IdempotentAppend_SurvivesRestartWithoutDuplicatingTheRecordAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        var record = Record("exactly-once") with { Idempotent = true };

        {
            var first = CreateStore(root);
            await using (first.ConfigureAwait(false))
            {
                var receipt = await first.AppendAsync(key, record).ConfigureAwait(false);
                receipt.Sequence.Should().Be(1);
            }
        }

        {
            var second = CreateStore(root);
            await using (second.ConfigureAwait(false))
            {
                var receipt = await second.AppendAsync(key, record).ConfigureAwait(false);
                receipt.Sequence.Should().Be(1);
                var page = await second.ReadAsync(
                    key,
                    1,
                    new DurableReadOptions(MaxScanBytes: 1024 * 1024)).ConfigureAwait(false);
                page.Records.Should().ContainSingle();
            }
        }
    }

    [Test]
    public async Task AppendModesExposeBufferedAndDurableFlushSemanticsAsync()
    {
        var root = CreateRoot();
        var store = CreateStore(root);
        await using var storeAsyncDisposal____ = store.ConfigureAwait(false);
        var key = DurableStreamKey.Process("runtime", Guid.NewGuid());

        store.GetSnapshot().LastSuccessfulFlush.Should().BeNull();
        await store.AppendAsync(key, Record("buffered"), DurableWriteMode.Buffered).ConfigureAwait(false);
        store.GetSnapshot().LastSuccessfulFlush.Should().BeNull();

        await store.AppendAsync(key, Record("durable"), DurableWriteMode.Durable).ConfigureAwait(false);
        store.GetSnapshot().LastSuccessfulFlush.Should().NotBeNull();
    }

    [Test]
    public async Task ReadAsync_ReadsLegacyAdditiveRecordBodyWithoutMigrationAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Process("runtime", Guid.NewGuid());
        var record = Record("legacy-body");
        {
            var writer = CreateUnencryptedStore(root);
            await using (writer.ConfigureAwait(false))
            {
                await writer.AppendAsync(key, record).ConfigureAwait(false);
                await writer.SealAsync(key).ConfigureAwait(false);
            }
        }

        var segment = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Should().ContainSingle().Subject;
        RewriteFirstFrameAsLegacyBody(segment);

        var recovered = CreateUnencryptedStore(root);
        await using var recoveredAsyncDisposal = recovered.ConfigureAwait(false);
        var page = await recovered.ReadAsync(
            key,
            1,
            new DurableReadOptions(MaxScanBytes: 1024 * 1024)).ConfigureAwait(false);

        var decoded = page.Records.Should().ContainSingle().Subject;
        decoded.Message.Should().Be("legacy-body");
        decoded.EventName.Should().Be(record.EventName);
        decoded.ExceptionText.Should().BeNull();
        decoded.Category.Should().BeNull();
        decoded.Properties.Should().BeNull();
    }

    [Test]
    public async Task BufferedIdempotentAppend_SealRebuildsADeletedDerivedIndexAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        var record = Record("terminal") with { Idempotent = true };

        {
            var first = CreateStore(root);
            await using (first.ConfigureAwait(false))
            {
                await first.AppendAsync(
                    key,
                    record,
                    DurableWriteMode.Buffered).ConfigureAwait(false);
                await first.SealAsync(key).ConfigureAwait(false);
            }
        }

        var index = Directory.GetFiles(
                root,
                ".idempotency",
                SearchOption.AllDirectories)
            .Should().ContainSingle().Which;
        File.Delete(index);

        var recovered = CreateStore(root);
        await using var recoveredAsyncDisposal_ = recovered.ConfigureAwait(false);
        var receipt = await recovered.AppendAsync(key, record).ConfigureAwait(false);
        receipt.Sequence.Should().Be(1);
        var page = await recovered.ReadAsync(
            key,
            1,
            new DurableReadOptions(MaxScanBytes: 1024 * 1024)).ConfigureAwait(false);
        page.Records.Should().ContainSingle();
    }

    [Test]
    public async Task ReadAsync_RejectsWrongKeysAndSealedSegmentCorruptionAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Process("runtime", Guid.NewGuid());
        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        {
            var writer = CreateStore(root, encryptionKey);
            await using (writer.ConfigureAwait(false))
            {
                await writer.AppendAsync(key, Record("protected")).ConfigureAwait(false);
            }
        }

        {
            var wrongKeyStore = CreateStore(
                         root,
                         RandomNumberGenerator.GetBytes(32));
            await using (wrongKeyStore.ConfigureAwait(false))
            {
                Func<Task> readWithWrongKey = async () =>
                    _ = await wrongKeyStore.ReadAsync(
                        key,
                        1,
                        new DurableReadOptions(MaxScanBytes: 1024 * 1024)).ConfigureAwait(false);
                await readWithWrongKey.Should().ThrowAsync<CryptographicException>().ConfigureAwait(false);
            }
        }

        var segment = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Should().ContainSingle().Which;
        var bytes = await File.ReadAllBytesAsync(segment).ConfigureAwait(false);
        bytes[48] ^= 0x40;
        await File.WriteAllBytesAsync(segment, bytes).ConfigureAwait(false);

        var corruptStore = CreateStore(root, encryptionKey);
        await using var corruptStoreAsyncDisposal = corruptStore.ConfigureAwait(false);
        Func<Task> readCorrupt = async () =>
            _ = await corruptStore.ReadAsync(
                key,
                1,
                new DurableReadOptions(MaxScanBytes: 1024 * 1024)).ConfigureAwait(false);
        await readCorrupt.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(false);
    }

    [Test]
    public async Task OperationalCatalogEnumeratesTypedBootsAcrossRestartWithoutReadingBodiesAsync()
    {
        var root = CreateRoot();
        var processBoot = Guid.Empty;
        var registrationBoot = Guid.NewGuid();
        var processKey = DurableStreamKey.Process("Runtime/Host", processBoot);
        var registrationKey = DurableStreamKey.Registration("Source/One", registrationBoot);
        try
        {
            {
                var store = CreateStore(root);
                await using (store.ConfigureAwait(false))
                {
                    await store.AppendAsync(processKey, Record("process")).ConfigureAwait(false);
                    await store.SealAsync(processKey).ConfigureAwait(false);
                    await store.AppendAsync(registrationKey, Record("module")).ConfigureAwait(false);

                    var processDirectory = new DurableStreamPathEncoder(root)
                        .GetStreamDirectory(processKey);
                    var processSegment = Directory.GetFiles(
                            processDirectory,
                            "*.scseg")
                        .Should().ContainSingle().Subject;
                    CorruptFramePayload(processSegment);

                    var catalog = await store.EnumerateOperationalStreamsAsync(
                        new DurableOperationalStreamEnumerationOptions
                        {
                            MaxEntries = 10,
                            MaxScanBytes = 1024 * 1024,
                            MaxDuration = TimeSpan.FromSeconds(2),
                        }).ConfigureAwait(false);

                    AssertOperationalCatalog(catalog, registrationBoot);
                }
            }

            var restarted = CreateStore(root);
            await using var restartedAsyncDisposal = restarted.ConfigureAwait(false);
            var afterRestart = await restarted.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 10,
                    MaxScanBytes = 1024 * 1024,
                    MaxDuration = TimeSpan.FromSeconds(2),
                }).ConfigureAwait(false);
            afterRestart.IdentityGaps.Should().BeEmpty();
            afterRestart.Streams.Select(summary => summary.Stream)
                .Should().Contain(processKey);
            afterRestart.Streams.Select(summary => summary.Stream)
                .Should().Contain(registrationKey);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Test]
    public async Task OperationalCatalogSkipsMalformedMetadataAndKeepsValidBootsAsync()
    {
        var root = CreateRoot();
        var invalidKey = DurableStreamKey.Process("invalid", Guid.NewGuid());
        var validKey = DurableStreamKey.Process("valid", Guid.NewGuid());
        try
        {
            {
                var store = CreateStore(root);
                await using (store.ConfigureAwait(false))
                {
                    await store.AppendAsync(invalidKey, Record("invalid")).ConfigureAwait(false);
                    await store.SealAsync(invalidKey).ConfigureAwait(false);
                    await store.AppendAsync(validKey, Record("valid")).ConfigureAwait(false);
                    await store.SealAsync(validKey).ConfigureAwait(false);
                }
            }

            var invalidDirectory = new DurableStreamPathEncoder(root)
                .GetStreamDirectory(invalidKey);
            await File.WriteAllTextAsync(
                Path.Combine(invalidDirectory, ".stream.manifest"),
                "{", TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);

            var reader = CreateStore(root);
            await using var readerAsyncDisposal = reader.ConfigureAwait(false);
            var catalog = await reader.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 10,
                    MaxScanBytes = 1024 * 1024,
                    MaxDuration = TimeSpan.FromSeconds(2),
                }).ConfigureAwait(false);

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
    public async Task OperationalCatalogReportsLegacyIdentityGapInsteadOfScanningBodiesAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Process("legacy", Guid.NewGuid());
        try
        {
            {
                var store = CreateStore(root);
                await using (store.ConfigureAwait(false))
                {
                    await store.AppendAsync(
                        key,
                        Record("legacy body")).ConfigureAwait(false);
                    await store.SealAsync(key).ConfigureAwait(false);
                }
            }

            var directory = new DurableStreamPathEncoder(root)
                .GetStreamDirectory(key);
            File.Delete(Path.Combine(directory, ".stream.identity"));

            var reader = CreateStore(root);
            await using var readerAsyncDisposal_ = reader.ConfigureAwait(false);
            var catalog = await reader.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 10,
                    MaxScanBytes = 1024 * 1024,
                    MaxDuration = TimeSpan.FromSeconds(2),
                }).ConfigureAwait(false);

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
    public async Task OperationalCatalogBoundsSummariesAndMetadataScanAsync()
    {
        var root = CreateRoot();
        try
        {
            {
                var store = CreateStore(root);
                await using (store.ConfigureAwait(false))
                {
                    foreach (var app in new[] { "first", "second" })
                    {
                        var key = DurableStreamKey.Process(app, Guid.NewGuid());
                        await store.AppendAsync(key, Record(app)).ConfigureAwait(false);
                        await store.SealAsync(key).ConfigureAwait(false);
                    }
                }
            }

            var reader = CreateStore(root);
            await using var readerAsyncDisposal__ = reader.ConfigureAwait(false);
            var entryBound = await reader.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 1,
                    MaxScanBytes = 1024 * 1024,
                    MaxDuration = TimeSpan.FromSeconds(2),
                }).ConfigureAwait(false);
            (entryBound.Streams.Count + entryBound.IdentityGaps.Count)
                .Should().BeLessThanOrEqualTo(1);
            entryBound.HasMore.Should().BeTrue();

            var scanBound = await reader.EnumerateOperationalStreamsAsync(
                new DurableOperationalStreamEnumerationOptions
                {
                    MaxEntries = 10,
                    MaxScanBytes = 1,
                    MaxDuration = TimeSpan.FromSeconds(2),
                }).ConfigureAwait(false);
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
        var key = DurableStreamKey.Registration("Source/With/Slashes", bootId);

        DurableStreamKey.TryParseOperational(
                key.CanonicalValue,
                out var parsed,
                out var appName,
                out var SourceId,
                out var parsedBootId)
            .Should().BeTrue();
        parsed.Should().Be(key);
        appName.Should().BeNull();
        SourceId.Should().Be("source/with/slashes");
        parsedBootId.Should().Be(Guid.Empty);
    }

    [Test]
    public async Task Retention_DeletesOnlyASealedPrefixAndPersistsExpiryWatermarksAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        var store = CreateStore(root);
        await using var storeAsyncDisposal_____ = store.ConfigureAwait(false);
        for (var index = 1; index <= 3; index++)
        {
            await store.AppendAsync(key, Record($"record-{index}")).ConfigureAwait(false);
            await store.SealAsync(key).ConfigureAwait(false);
        }

        var segments = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        File.SetLastWriteTimeUtc(segments[0], DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(segments[1], DateTime.UtcNow.AddDays(-10));

        var result = await store.ApplyRetentionAsync(new DurableRetentionOptions
        {
            JobLogAge = TimeSpan.FromDays(1),
            ProcessLogAge = TimeSpan.FromDays(30),
            RegistrationLogAge = TimeSpan.FromDays(30),
            MaximumEncodedBytes = long.MaxValue,
            MinimumFreeBytes = 0,
        }).ConfigureAwait(false);

        result.DeletedSegments.Should().Be(2);
        var summary = await store.GetSummaryAsync(key).ConfigureAwait(false);
        summary.FirstAvailableSequence.Should().Be(3);
        summary.ExpiredRecordCount.Should().Be(2);
        var page = await store.ReadAsync(
            key,
            1,
            new DurableReadOptions(MaxScanBytes: 1024 * 1024)).ConfigureAwait(false);
        page.Records.Select(record => record.Sequence).Should().Equal(3);
        page.FirstAvailableSequence.Should().Be(3);
        page.ExpiredRecordCount.Should().Be(2);
    }

    [Test]
    public async Task ArtifactReferenceIndex_TracksRetainedRecordsAndPrunesExpiredPrefixesAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        var artifactId = Guid.NewGuid();
        var store = CreateStore(root);
        await using var storeAsyncDisposal______ = store.ConfigureAwait(false);
        await store.AppendAsync(
            key,
            Record("externalized") with
            {
                Artifact = new DurableArtifactReference(
                    artifactId,
                    "text/plain",
                    12,
                    new string('a', 64)),
            }).ConfigureAwait(false);
        await store.SealAsync(key).ConfigureAwait(false);

        (await store.ReadArtifactReferencesAsync().ConfigureAwait(false)).Should().Contain(artifactId);
        var segment = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Should().ContainSingle().Which;
        File.SetLastWriteTimeUtc(segment, DateTime.UtcNow.AddDays(-10));

        await store.ApplyRetentionAsync(new DurableRetentionOptions
        {
            JobLogAge = TimeSpan.FromDays(1),
            ProcessLogAge = TimeSpan.FromDays(30),
            RegistrationLogAge = TimeSpan.FromDays(30),
            MaximumEncodedBytes = long.MaxValue,
            MinimumFreeBytes = 0,
        }).ConfigureAwait(false);

        (await store.ReadArtifactReferencesAsync().ConfigureAwait(false)).Should().NotContain(artifactId);
    }

    [Test]
    public async Task Retention_RecoversAndExpiresAnUntrackedCrashOpenSegmentAsync()
    {
        var root = CreateRoot();
        var key = DurableStreamKey.Job(Guid.NewGuid());
        {
            var writer = CreateStore(root);
            await using (writer.ConfigureAwait(false))
                await writer.AppendAsync(key, Record("crash tail")).ConfigureAwait(false);
        }
        var sealedPath = Directory.GetFiles(root, "*.scseg", SearchOption.AllDirectories)
            .Should().ContainSingle().Which;
        var openPath = Path.ChangeExtension(sealedPath, ".open");
        File.Move(sealedPath, openPath);
        File.SetLastWriteTimeUtc(openPath, DateTime.UtcNow.AddDays(-10));

        var recovered = CreateStore(root);
        await using var recoveredAsyncDisposal__ = recovered.ConfigureAwait(false);
        var result = await recovered.ApplyRetentionAsync(new DurableRetentionOptions
        {
            JobLogAge = TimeSpan.FromDays(1),
            ProcessLogAge = TimeSpan.FromDays(30),
            RegistrationLogAge = TimeSpan.FromDays(30),
            MaximumEncodedBytes = long.MaxValue,
            MinimumFreeBytes = 0,
        }).ConfigureAwait(false);

        result.DeletedSegments.Should().Be(1);
        Directory.GetFiles(root, "*.open", SearchOption.AllDirectories)
            .Should().BeEmpty();
        var summary = await recovered.GetSummaryAsync(key).ConfigureAwait(false);
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

    private static DurableSegmentStore CreateUnencryptedStore(string root) =>
        new(new DurableStorageOptions
        {
            RootDirectory = root,
            EncryptionKey = null,
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

    private static List<int> ReadFrameEncodedBytes(string path)
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

    private static void RewriteFirstFrameAsLegacyBody(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (segmentId, sequence, recordId, timestamp, flags, oldBodyLength, oldPayload) = ReadFrameParts(bytes);

        var legacyBody = CreateLegacyRecordBody(oldPayload, oldBodyLength);
        var compressedLegacyBody = Compress(legacyBody);
        var associatedData = BuildAssociatedData(
            segmentId,
            sequence,
            recordId,
            timestamp,
            flags,
            legacyBody.Length);
        var digest = ComputeFrameDigest(associatedData, compressedLegacyBody);

        using var newFrameStream = new MemoryStream();
        using (var frameWriter = new BinaryWriter(
                   newFrameStream,
                   Encoding.UTF8,
                   leaveOpen: true))
        {
            frameWriter.Write(sequence);
            frameWriter.Write(recordId.ToByteArray());
            frameWriter.Write(timestamp);
            frameWriter.Write(flags);
            frameWriter.Write(legacyBody.Length);
            frameWriter.Write(new byte[12]);
            frameWriter.Write(new byte[16]);
            frameWriter.Write(digest);
            frameWriter.Write(compressedLegacyBody.Length);
            frameWriter.Write(compressedLegacyBody);
        }
        var newFrame = newFrameStream.ToArray();

        using var output = new MemoryStream();
        output.Write(bytes, 0, 40);
        using (var writer = new BinaryWriter(
                   output,
                   Encoding.UTF8,
                   leaveOpen: true))
        {
            writer.Write(newFrame.Length);
            writer.Write(newFrame);
            writer.Flush();
        }
        var prefixDigest = SHA256.HashData(output.ToArray());
        using (var footerWriter = new BinaryWriter(
                   output,
                   Encoding.UTF8,
                   leaveOpen: true))
        {
            footerWriter.Write(-1);
            footerWriter.Write(sequence);
            footerWriter.Write(1L);
            footerWriter.Write(prefixDigest);
        }
        File.WriteAllBytes(path, output.ToArray());
    }

    private static void CorruptFramePayload(string processSegment)
    {
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
            // Flush(true) durably installs corruption; FlushAsync has no fsync option.
#pragma warning disable CA1849
            corrupt.Flush(flushToDisk: true);
#pragma warning restore CA1849
        }
    }

    private static void AssertOperationalCatalog(DurableOperationalStreamCatalog catalog, Guid registrationBoot)
    {
        catalog.IdentityGaps.Should().BeEmpty();
        catalog.Streams.Should().HaveCount(2);
        var process = catalog.Streams.Where(summary =>
            summary.Stream.Kind == DurableStreamKind.ProcessLog).Should().ContainSingle().Which;
        process.AppName.Should().Be("runtime/host");
        process.SourceId.Should().BeNull();
        process.BootId.Should().Be(Guid.Empty);
        process.HasActiveSegment.Should().BeFalse();
        process.HasSealedSegments.Should().BeTrue();
        process.RecordCount.Should().Be(1);
        process.FirstAvailableSequence.Should().Be(1);

        var module = catalog.Streams.Where(summary =>
            summary.Stream.Kind == DurableStreamKind.RegistrationLog).Should().ContainSingle().Which;
        module.AppName.Should().BeNull();
        module.SourceId.Should().Be("source/one");
        module.BootId.Should().Be(registrationBoot);
        module.HasActiveSegment.Should().BeTrue();
        module.HasSealedSegments.Should().BeFalse();
        module.RecordCount.Should().Be(1);
    }

    private static (Guid SegmentId, long Sequence, Guid RecordId, long Timestamp, byte Flags, int BodyLength, byte[] Payload)
        ReadFrameParts(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(input);
        input.Position = 8;
        var segmentId = new Guid(reader.ReadBytes(16));
        input.Position = 40;
        var oldFrameLength = reader.ReadInt32();
        var oldFrame = reader.ReadBytes(oldFrameLength);
        using var oldFrameStream = new MemoryStream(oldFrame, writable: false);
        using var oldFrameReader = new BinaryReader(oldFrameStream);
        var sequence = oldFrameReader.ReadInt64();
        var recordId = new Guid(oldFrameReader.ReadBytes(16));
        var timestamp = oldFrameReader.ReadInt64();
        var flags = oldFrameReader.ReadByte();
        var oldBodyLength = oldFrameReader.ReadInt32();
        oldFrameReader.ReadBytes(12);
        oldFrameReader.ReadBytes(16);
        oldFrameReader.ReadBytes(32);
        var oldPayloadLength = oldFrameReader.ReadInt32();
        var oldPayload = oldFrameReader.ReadBytes(oldPayloadLength);
        oldPayload.Should().HaveCount(oldPayloadLength);
        flags.Should().Be(2);

        return (segmentId, sequence, recordId, timestamp, flags, oldBodyLength, oldPayload);
    }

    private static byte[] CreateLegacyRecordBody(byte[] oldPayload, int oldBodyLength)
    {
        byte[] oldBody;
        using (var compressed = new MemoryStream(oldPayload, writable: false))
        using (var brotli = new BrotliStream(compressed, CompressionMode.Decompress))
        using (var body = new MemoryStream())
        {
            brotli.CopyTo(body);
            oldBody = body.ToArray();
        }
        oldBody.Should().HaveCount(oldBodyLength);

        var originalFields = new HashSet<string>(
            [
                "Level",
                "EventName",
                "Message",
                "ExceptionType",
                "CorrelationId",
                "Artifact",
            ],
            StringComparer.Ordinal);
        using var document = JsonDocument.Parse(oldBody);
        using var legacyBodyStream = new MemoryStream();
        using (var jsonWriter = new Utf8JsonWriter(legacyBodyStream))
        {
            jsonWriter.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!originalFields.Contains(property.Name))
                    continue;
                jsonWriter.WritePropertyName(property.Name);
                property.Value.WriteTo(jsonWriter);
            }
            jsonWriter.WriteEndObject();
        }
        return legacyBodyStream.ToArray();
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(
                   output,
                   CompressionLevel.Fastest,
                   leaveOpen: true))
        {
            brotli.Write(source);
        }
        return output.ToArray();
    }

    private static byte[] BuildAssociatedData(
        Guid segmentId,
        long sequence,
        Guid recordId,
        long timestamp,
        byte flags,
        int bodyLength)
    {
        var data = new byte[53];
        segmentId.TryWriteBytes(data);
        BitConverter.TryWriteBytes(data.AsSpan(16), sequence);
        recordId.TryWriteBytes(data.AsSpan(24));
        BitConverter.TryWriteBytes(data.AsSpan(40), timestamp);
        data[48] = flags;
        BitConverter.TryWriteBytes(data.AsSpan(49), bodyLength);
        return data;
    }

    private static byte[] ComputeFrameDigest(
        byte[] associatedData,
        byte[] compressed)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(associatedData);
        hash.AppendData(compressed);
        return hash.GetHashAndReset();
    }
}
