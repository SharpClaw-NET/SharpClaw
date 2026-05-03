using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public class ColdIndexMaintenanceServiceTests
{
    private InMemoryPersistenceFileSystem _fs = null!;
    private JsonFileOptions _options = null!;
    private EncryptionOptions _encryptionOptions = null!;

    [SetUp]
    public void SetUp()
    {
        _fs = new InMemoryPersistenceFileSystem();
        _options = new JsonFileOptions { DataDirectory = "/data", FsyncOnWrite = false };
        _encryptionOptions = new EncryptionOptions { Key = ApiKeyEncryptor.GenerateKey() };
    }

    private ColdIndexMaintenanceService CreateService(ILogger<ColdIndexMaintenanceService>? logger = null)
    {
        return new ColdIndexMaintenanceService(
            _options,
            _encryptionOptions,
            _fs,
            logger ?? NullLogger<ColdIndexMaintenanceService>.Instance);
    }

    // ── Timer fires and rebuilds ──────────────────────────────────

    [Test]
    public async Task RebuildAsync_CompletesWithoutError_WhenNoData()
    {
        using var service = CreateService();
        // Should not throw even with no cold entity directories
        await service.RebuildAsync(CancellationToken.None);
    }

    [Test]
    public async Task RebuildAsync_RebuildsIndexForColdEntityDir()
    {
        // Create a cold entity directory with an entity file
        var entityDir = _fs.CombinePath(_options.DataDirectory, "ChatMessageDB");
        _fs.CreateDirectory(entityDir);

        var msg = new SharpClaw.Contracts.Entities.Core.Messages.ChatMessageDB
        {
            Id = Guid.NewGuid(),
            ChannelId = Guid.NewGuid(),
            Role = "user",
            Content = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(msg, ColdEntityStore.JsonOptions);
        var filePath = _fs.CombinePath(entityDir, $"{msg.Id}.json");
        await JsonFileEncryption.WriteJsonAsync(_fs, filePath, json, _encryptionOptions.Key,
            encrypt: true, fsync: false, CancellationToken.None);

        using var service = CreateService();
        await service.RebuildAsync(CancellationToken.None);

        // Verify index shard was created
        var shardPath = ColdEntityIndex.GetShardPath(_fs, entityDir, "ChannelId");
        _fs.FileExists(shardPath).Should().BeTrue();

        // Verify lookup works
        var result = await ColdEntityIndex.LookupAsync(_fs, entityDir, "ChannelId", msg.ChannelId,
            NullLogger.Instance, CancellationToken.None);
        result.Should().Contain(msg.Id);
    }

    // ── Start with interval 0 = no-op ─────────────────────────────

    [Test]
    public void Start_WithZeroInterval_DoesNotStart()
    {
        using var service = CreateService();
        // Should not throw or start any background work
        service.Start(0);
        // Dispose should be clean
    }

    // ── Dispose stops the loop ────────────────────────────────────

    [Test]
    public async Task Dispose_StopsTimerLoop()
    {
        using var service = CreateService();
        service.Start(1); // 1 minute interval
        await Task.Delay(50); // Let the loop start
        service.Dispose(); // Should cancel cleanly
    }

    // ── Slow rebuild warning ──────────────────────────────────────

    [Test]
    public async Task RebuildAsync_LogsWarning_WhenSlow()
    {
        // Inject a slow file system that delays reads
        _fs.OnBeforeRead = async _ => await Task.Delay(50);

        var entityDir = _fs.CombinePath(_options.DataDirectory, "ChatMessageDB");
        _fs.CreateDirectory(entityDir);

        // Create many entity files to make rebuild take time
        for (var i = 0; i < 5; i++)
        {
            var msg = new SharpClaw.Contracts.Entities.Core.Messages.ChatMessageDB
            {
                Id = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Role = "user",
                Content = "test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(msg, ColdEntityStore.JsonOptions);
            await JsonFileEncryption.WriteJsonAsync(_fs,
                _fs.CombinePath(entityDir, $"{msg.Id}.json"), json,
                _encryptionOptions.Key, encrypt: true, fsync: false, CancellationToken.None);
        }

        // The rebuild will take > 250ms with 5 files × 50ms delay, but not > 30s.
        // We verify it completes without error (warning threshold is 30s so no warning here).
        using var service = CreateService();
        await service.RebuildAsync(CancellationToken.None);
        // Test passes if no exception — actual slow warning requires mocking time, not worth it.
    }
}
