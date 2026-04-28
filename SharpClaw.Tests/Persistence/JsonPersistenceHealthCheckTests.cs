using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;

namespace SharpClaw.Tests.Persistence;

/// <summary>
/// Phase M tests: JsonPersistenceHealthCheck — disk writable, pending transactions,
/// quarantined files, checksums, event log, snapshot age, unclean shutdown, flush backlog.
/// </summary>
[TestFixture]
public class JsonPersistenceHealthCheckTests
{
    private string _dataDir = null!;
    private IPersistenceFileSystem _fs = null!;
    private JsonFileOptions _options = null!;
    private EncryptionOptions _encOptions = null!;
    private TransactionQueue _txQueue = null!;
    private ILogger<JsonPersistenceHealthCheck> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"healthcheck_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _fs = new PhysicalPersistenceFileSystem();
        _options = new JsonFileOptions
        {
            DataDirectory = _dataDir,
            EnableChecksums = false,
            EnableEventLog = false,
            EnableSnapshots = false,
            AsyncFlush = false,
        };
        _encOptions = new EncryptionOptions { Key = [] };
        _txQueue = new TransactionQueue(
            _fs, _options, NullLogger<TransactionQueue>.Instance);
        _logger = NullLogger<JsonPersistenceHealthCheck>.Instance;
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private JsonPersistenceHealthCheck MakeCheck(FlushQueue? flushQueue = null)
        => new(_fs, _options, _encOptions, _txQueue, _logger, flushQueue);

    // ── Healthy baseline ──────────────────────────────────────────

    [Test]
    public async Task AllHealthy_WhenNothingWrong()
    {
        var result = await MakeCheck().CheckAsync();

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Entries.Should().AllSatisfy(e =>
            e.Status.Should().Be(HealthStatus.Healthy));
    }

    // ── Disk writable ─────────────────────────────────────────────

    [Test]
    public async Task DiskWritable_Healthy()
    {
        var result = await MakeCheck().CheckAsync();
        var entry = result.Entries.First(e => e.Name == "DiskWritable");
        entry.Status.Should().Be(HealthStatus.Healthy);
    }

    // ── Pending transactions ──────────────────────────────────────

    [Test]
    public async Task PendingTransactions_Degraded_WhenPendingExist()
    {
        var pendingDir = Path.Combine(_dataDir, "_transactions", "pending");
        Directory.CreateDirectory(pendingDir);
        await File.WriteAllTextAsync(Path.Combine(pendingDir, "001.json"), "{}");

        var result = await MakeCheck().CheckAsync();
        var entry = result.Entries.First(e => e.Name == "PendingTransactions");
        entry.Status.Should().Be(HealthStatus.Degraded);
        entry.Description.Should().Contain("1 pending");
    }

    // ── Quarantined files ─────────────────────────────────────────

    [Test]
    public async Task QuarantinedFiles_Degraded_WhenQuarantineNotEmpty()
    {
        var entityDir = Path.Combine(_dataDir, "TestEntity");
        var quarantineDir = Path.Combine(entityDir, QuarantineService.QuarantineDir);
        Directory.CreateDirectory(quarantineDir);
        await File.WriteAllTextAsync(Path.Combine(quarantineDir, "corrupt.json"), "{}");

        var result = await MakeCheck().CheckAsync();
        var entry = result.Entries.First(e => e.Name == "QuarantinedFiles");
        entry.Status.Should().Be(HealthStatus.Degraded);
        entry.Description.Should().Contain("1 quarantined");
    }

    // ── Flush queue backlog ───────────────────────────────────────

    [Test]
    public async Task FlushQueueBacklog_Healthy_WhenDisabled()
    {
        _options.AsyncFlush = false;
        var result = await MakeCheck().CheckAsync();
        var entry = result.Entries.First(e => e.Name == "FlushQueueBacklog");
        entry.Status.Should().Be(HealthStatus.Healthy);
        entry.Description.Should().Contain("disabled");
    }

    [Test]
    public async Task FlushQueueBacklog_Healthy_WhenEmpty()
    {
        _options.AsyncFlush = true;
        var queue = new FlushQueue(NullLogger<FlushQueue>.Instance, capacity: 10);
        var result = await MakeCheck(queue).CheckAsync();
        var entry = result.Entries.First(e => e.Name == "FlushQueueBacklog");
        entry.Status.Should().Be(HealthStatus.Healthy);
        queue.Dispose();
    }

    // ── Event log writable ────────────────────────────────────────

    [Test]
    public async Task EventLogWritable_Healthy_WhenEnabled()
    {
        _options.EnableEventLog = true;
        var result = await MakeCheck().CheckAsync();
        var entry = result.Entries.First(e => e.Name == "EventLogWritable");
        entry.Status.Should().Be(HealthStatus.Healthy);
    }

    [Test]
    public async Task EventLogWritable_Skipped_WhenDisabled()
    {
        _options.EnableEventLog = false;
        var result = await MakeCheck().CheckAsync();
        result.Entries.Should().NotContain(e => e.Name == "EventLogWritable");
    }

    // ── Snapshot age ──────────────────────────────────────────────

    [Test]
    public async Task SnapshotAge_Degraded_WhenNoSnapshots()
    {
        _options.EnableSnapshots = true;
        var result = await MakeCheck().CheckAsync();
        var entry = result.Entries.First(e => e.Name == "SnapshotAge");
        entry.Status.Should().Be(HealthStatus.Degraded);
    }

    [Test]
    public async Task SnapshotAge_Healthy_WhenRecentSnapshotExists()
    {
        _options.EnableSnapshots = true;
        _options.SnapshotIntervalHours = 24;
        var snapshotsDir = Path.Combine(_dataDir, SnapshotService.SnapshotsDirectory);
        Directory.CreateDirectory(snapshotsDir);
        var timestamp = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyyMMdd_HHmmss",
            System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllBytesAsync(
            Path.Combine(snapshotsDir, $"{SnapshotService.FilePrefix}{timestamp}{SnapshotService.FileExtension}"),
            [0x50, 0x4B]); // minimal ZIP header bytes

        var result = await MakeCheck().CheckAsync();
        var entry = result.Entries.First(e => e.Name == "SnapshotAge");
        entry.Status.Should().Be(HealthStatus.Healthy);
    }

    // ── Unclean shutdown sentinel ─────────────────────────────────

    [Test]
    public async Task UncleanShutdown_Healthy_WhenNoSentinel()
    {
        var result = await MakeCheck().CheckAsync();
        var entry = result.Entries.First(e => e.Name == "UncleanShutdown");
        entry.Status.Should().Be(HealthStatus.Healthy);
    }

    [Test]
    public async Task UncleanShutdown_Degraded_WhenSentinelPresent()
    {
        JsonPersistenceHealthCheck.CreateSentinel(_fs, _options);
        var result = await MakeCheck().CheckAsync();
        var entry = result.Entries.First(e => e.Name == "UncleanShutdown");
        entry.Status.Should().Be(HealthStatus.Degraded);
    }

    [Test]
    public void Sentinel_CreateAndRemove_RoundTrip()
    {
        JsonPersistenceHealthCheck.CreateSentinel(_fs, _options);
        var path = Path.Combine(_dataDir, JsonPersistenceHealthCheck.UncleanShutdownSentinel);
        File.Exists(path).Should().BeTrue();

        JsonPersistenceHealthCheck.RemoveSentinel(_fs, _options);
        File.Exists(path).Should().BeFalse();
    }

    // ── Aggregate status ──────────────────────────────────────────

    [Test]
    public async Task AggregateStatus_Unhealthy_WhenAnyUnhealthy()
    {
        // Create sentinel for degraded, then make flush queue near capacity for unhealthy.
        _options.AsyncFlush = true;
        JsonPersistenceHealthCheck.CreateSentinel(_fs, _options);

        var queue = new FlushQueue(NullLogger<FlushQueue>.Instance, capacity: 2);
        // Fill queue to capacity (90%+ → unhealthy): enqueue 2 items into capacity-2 queue.
        var intent = MakeIntent();
        await queue.EnqueueAsync(intent);
        await queue.EnqueueAsync(intent);

        var result = await MakeCheck(queue).CheckAsync();
        result.Status.Should().Be(HealthStatus.Unhealthy);

        queue.Dispose();
    }

    private static FlushQueue.FlushIntent MakeIntent() => new(
        EntityChanges: [],
        JoinTableChanges: new HashSet<string>(),
        SerializedEntities: new Dictionary<(string, Guid), byte[]>(),
        SerializedEntitiesByClrType: new Dictionary<(Type, Guid), byte[]>());
}
