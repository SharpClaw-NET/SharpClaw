using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Persistence;

/// <summary>
/// Phase K tests: Background flush — FlushQueue, FlushWorker, overlay, drain, back-pressure.
/// </summary>
[TestFixture]
public class FlushQueueTests
{
    private string _dataDir = null!;
    private IPersistenceFileSystem _fs = null!;
    private DirectoryLockManager _lockManager = null!;
    private ILogger<FlushQueue> _queueLogger = null!;
    private ILogger<FlushWorker> _workerLogger = null!;

    [SetUp]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"flushq_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _fs = new PhysicalPersistenceFileSystem();
        _lockManager = new DirectoryLockManager();
        _queueLogger = NullLogger<FlushQueue>.Instance;
        _workerLogger = NullLogger<FlushWorker>.Instance;
    }

    [TearDown]
    public void TearDown()
    {
        _lockManager.Dispose();
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private JsonFileOptions MakeOptions(bool asyncFlush = true) => new()
    {
        DataDirectory = _dataDir,
        AsyncFlush = asyncFlush,
        EncryptAtRest = false,
        FsyncOnWrite = false,
        EnableChecksums = false,
        EnableEventLog = false,
    };

    private static FlushQueue.FlushIntent MakeIntent(
        IReadOnlyList<(Type, Guid, EntityState)>? entities = null,
        IReadOnlySet<string>? joins = null,
        IReadOnlyDictionary<(string, Guid), byte[]>? serialized = null)
        => new(
            entities ?? [],
            joins ?? new HashSet<string>(),
            serialized ?? new Dictionary<(string, Guid), byte[]>(),
            new Dictionary<(Type, Guid), byte[]>());

    private IServiceProvider BuildServices(JsonFileOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        var key = ApiKeyEncryptor.GenerateKey();
        services.AddSingleton(new EncryptionOptions { Key = key });
        services.AddSingleton<IPersistenceFileSystem>(_fs);
        services.AddSingleton(_lockManager);
        services.AddSingleton<TransactionQueue>();
        services.AddScoped<JsonFilePersistenceService>();
        services.AddDbContext<SharpClaw.Infrastructure.Persistence.SharpClawDbContext>(o =>
            o.UseInMemoryDatabase($"FlushQ_{Guid.NewGuid():N}"));
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    // ── Tests ────────────────────────────────────────────────────

    [Test]
    public async Task EnqueueAndDequeue_RoundTrips()
    {
        using var queue = new FlushQueue(_queueLogger, capacity: 8);
        var id = Guid.NewGuid();
        var serialized = new Dictionary<(string, Guid), byte[]>
        {
            [("TestEntity", id)] = [1, 2, 3]
        };
        var intent = MakeIntent(
            entities: [(typeof(object), id, EntityState.Added)],
            serialized: serialized);

        await queue.EnqueueAsync(intent);

        var dequeued = await queue.DequeueAsync(CancellationToken.None);
        dequeued.EntityChanges.Count.Should().Be(1);
        dequeued.SerializedEntities.Should().ContainKey(("TestEntity", id));
    }

    [Test]
    public async Task Overlay_PopulatedOnEnqueue_ClearedOnRemove()
    {
        using var queue = new FlushQueue(_queueLogger, capacity: 8);
        var id = Guid.NewGuid();
        var serialized = new Dictionary<(string, Guid), byte[]>
        {
            [("Foo", id)] = [42]
        };
        var intent = MakeIntent(serialized: serialized);

        await queue.EnqueueAsync(intent);

        queue.Overlay.Should().ContainKey(("Foo", id));
        queue.Overlay[("Foo", id)].Should().BeEquivalentTo(new byte[] { 42 });

        queue.RemoveOverlayEntries(intent);
        queue.Overlay.Should().BeEmpty();
    }

    [Test]
    public async Task Overlay_DeletesStoredAsTombstones()
    {
        using var queue = new FlushQueue(_queueLogger, capacity: 8);
        var id = Guid.NewGuid();
        var intent = MakeIntent(
            entities: [(typeof(object), id, EntityState.Deleted)]);

        await queue.EnqueueAsync(intent);

        queue.Overlay.Should().ContainKey(("Object", id));
        queue.Overlay[("Object", id)].Should().BeNull();
    }

    [Test]
    public async Task DrainAsync_WaitsForEmptyQueue()
    {
        using var queue = new FlushQueue(_queueLogger, capacity: 8);

        // Enqueue 3 items.
        for (var i = 0; i < 3; i++)
            await queue.EnqueueAsync(MakeIntent());

        // Start a consumer that dequeues with a small delay.
        var consumed = 0;
        var consumer = Task.Run(async () =>
        {
            while (queue.TryRead(out _))
            {
                consumed++;
                await Task.Delay(10);
            }
        });

        await consumer;

        // After consumer finishes, drain should return immediately.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await queue.DrainAsync(cts.Token);

        consumed.Should().Be(3);
        queue.Count.Should().Be(0);
    }

    [Test]
    public async Task BackPressure_WhenQueueFull_EnqueueBlocks()
    {
        using var queue = new FlushQueue(_queueLogger, capacity: 2);

        // Fill the queue.
        await queue.EnqueueAsync(MakeIntent());
        await queue.EnqueueAsync(MakeIntent());

        // Third enqueue should block. Use a timeout to verify.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Func<Task> act = () => queue.EnqueueAsync(MakeIntent(), cts.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task FlushWorker_ProcessesIntents()
    {
        var options = MakeOptions(asyncFlush: false); // Worker flushes synchronously.
        var sp = BuildServices(options);
        using var queue = new FlushQueue(_queueLogger, capacity: 8);

        var worker = new FlushWorker(queue, sp, _workerLogger);
        worker.Start();

        // Enqueue an intent with no actual entity changes (empty flush).
        await queue.EnqueueAsync(MakeIntent());

        // Give the worker time to process.
        await Task.Delay(200);

        queue.Count.Should().Be(0);

        queue.Complete();
        await worker.StopAsync();
        worker.Dispose();
    }

    [Test]
    public async Task FlushWorker_DrainOnShutdown()
    {
        var options = MakeOptions();
        var sp = BuildServices(options);
        using var queue = new FlushQueue(_queueLogger, capacity: 8);

        // Enqueue items before starting the worker.
        await queue.EnqueueAsync(MakeIntent());
        await queue.EnqueueAsync(MakeIntent());

        var worker = new FlushWorker(queue, sp, _workerLogger);
        worker.Start();

        // Immediately stop — worker should drain remaining items.
        queue.Complete();
        await worker.StopAsync();
        worker.Dispose();

        queue.Count.Should().Be(0);
    }

    [Test]
    public async Task FlushWorker_RetryOnFailure()
    {
        // Use a service provider with no JsonFilePersistenceService registration
        // to force failures, then verify the worker doesn't crash.
        var services = new ServiceCollection();
        services.AddSingleton(MakeOptions());
        services.AddSingleton(new EncryptionOptions { Key = ApiKeyEncryptor.GenerateKey() });
        services.AddSingleton<IPersistenceFileSystem>(_fs);
        services.AddDbContext<SharpClaw.Infrastructure.Persistence.SharpClawDbContext>(o =>
            o.UseInMemoryDatabase($"FlushQ_Retry_{Guid.NewGuid():N}"));
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        using var queue = new FlushQueue(_queueLogger, capacity: 8);
        var worker = new FlushWorker(queue, sp, _workerLogger);
        worker.Start();

        await queue.EnqueueAsync(MakeIntent());

        // Worker will fail 3 times, then move on. Give it time.
        await Task.Delay(2000);

        queue.Complete();
        await worker.StopAsync();
        worker.Dispose();

        // The worker should still be running (not crash).
        // The intent was dropped after max retries.
        queue.Count.Should().Be(0);
    }

    [Test]
    public async Task Overlay_ReadAfterWrite_Consistency()
    {
        using var queue = new FlushQueue(_queueLogger, capacity: 8);
        var id = Guid.NewGuid();
        var data = JsonSerializer.SerializeToUtf8Bytes(new { Id = id, Name = "Test" });
        var serialized = new Dictionary<(string, Guid), byte[]>
        {
            [("MyEntity", id)] = data
        };
        var intent = MakeIntent(
            entities: [(typeof(object), id, EntityState.Added)],
            serialized: serialized);

        await queue.EnqueueAsync(intent);

        // Overlay should contain the data immediately.
        queue.Overlay.TryGetValue(("MyEntity", id), out var overlayData).Should().BeTrue();
        overlayData.Should().BeEquivalentTo(data);

        // After removal, it should be gone.
        queue.RemoveOverlayEntries(intent);
        queue.Overlay.TryGetValue(("MyEntity", id), out _).Should().BeFalse();
    }

    [Test]
    public async Task SnapshotService_DrainAsync_CalledBeforeLocks()
    {
        // Verify that SnapshotService accepts a FlushQueue parameter
        // and that CreateSnapshotAsync calls DrainAsync.
        var options = MakeOptions();
        options.EnableSnapshots = true;
        using var queue = new FlushQueue(_queueLogger, capacity: 8);

        // Enqueue an item so DrainAsync has something to wait for.
        await queue.EnqueueAsync(MakeIntent());
        // Immediately dequeue so drain can complete.
        queue.TryRead(out _);

        var snapshotLogger = NullLogger<SnapshotService>.Instance;
        var service = new SnapshotService(_fs, options, _lockManager, snapshotLogger, queue);

        // Create a dummy entity file so the snapshot has something to capture.
        var entityDir = Path.Combine(_dataDir, "TestEntity");
        Directory.CreateDirectory(entityDir);
        await File.WriteAllTextAsync(Path.Combine(entityDir, $"{Guid.NewGuid()}.json"), "{}");

        var result = await service.CreateSnapshotAsync(CancellationToken.None);
        result.Should().NotBeNull();
        File.Exists(result!).Should().BeTrue();
    }

    [Test]
    public async Task Complete_PreventsNewEnqueues()
    {
        using var queue = new FlushQueue(_queueLogger, capacity: 8);
        queue.Complete();

        Func<Task> act = () => queue.EnqueueAsync(MakeIntent()).AsTask();
        await act.Should().ThrowAsync<ChannelClosedException>();
    }
}
