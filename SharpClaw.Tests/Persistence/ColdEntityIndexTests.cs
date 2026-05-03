using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public class ColdEntityIndexTests
{
    private string _entityDir = null!;
    private byte[] _key = null!;
    private IPersistenceFileSystem _fs = null!;

    [SetUp]
    public void SetUp()
    {
        _entityDir = Path.Combine(Path.GetTempPath(), $"idx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_entityDir);
        _key = ApiKeyEncryptor.GenerateKey();
        _fs = new PhysicalPersistenceFileSystem();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_entityDir))
            Directory.Delete(_entityDir, recursive: true);
    }

    private static ChatMessageDB CreateMessage(Guid? id = null, Guid? channelId = null, Guid? threadId = null)
    {
        return new ChatMessageDB
        {
            Id = id ?? Guid.NewGuid(),
            ChannelId = channelId ?? Guid.NewGuid(),
            ThreadId = threadId,
            Role = "user",
            Content = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ── UpdateIndexAsync + LookupAsync round-trip ─────────────────

    [Test]
    public async Task UpdateAndLookup_RoundTrips()
    {
        var channelId = Guid.NewGuid();
        var msg = CreateMessage(channelId: channelId);

        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        var result = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, nameof(ChatMessageDB.ChannelId), channelId,
            NullLogger.Instance, CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().Contain(msg.Id);
    }

    [Test]
    public async Task Lookup_MultipleEntitiesSameKey()
    {
        var channelId = Guid.NewGuid();
        var msg1 = CreateMessage(channelId: channelId);
        var msg2 = CreateMessage(channelId: channelId);

        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg1.Id, msg1,
            deleted: false, NullLogger.Instance, CancellationToken.None);
        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg2.Id, msg2,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        var result = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, nameof(ChatMessageDB.ChannelId), channelId,
            NullLogger.Instance, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain(msg1.Id);
        result.Should().Contain(msg2.Id);
    }

    [Test]
    public async Task Lookup_ReturnsNull_WhenKeyNotInIndex()
    {
        var msg = CreateMessage();
        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        var result = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, nameof(ChatMessageDB.ChannelId), Guid.NewGuid(),
            NullLogger.Instance, CancellationToken.None);

        result.Should().BeNull();
    }

    [Test]
    public async Task Lookup_ReturnsNull_WhenNoIndexFile()
    {
        var result = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, "ChannelId", Guid.NewGuid(),
            NullLogger.Instance, CancellationToken.None);

        result.Should().BeNull();
    }

    // ── Delete removes from index ─────────────────────────────────

    [Test]
    public async Task Delete_RemovesEntityFromIndex()
    {
        var channelId = Guid.NewGuid();
        var msg = CreateMessage(channelId: channelId);

        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        // Delete
        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: true, NullLogger.Instance, CancellationToken.None);

        var result = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, nameof(ChatMessageDB.ChannelId), channelId,
            NullLogger.Instance, CancellationToken.None);

        // Key pruned because list became empty → returns null
        result.Should().BeNull();
    }

    // ── ThreadId (nullable FK) ────────────────────────────────────

    [Test]
    public async Task Indexes_NullableProperty_WhenNotNull()
    {
        var threadId = Guid.NewGuid();
        var msg = CreateMessage(threadId: threadId);

        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        var result = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, nameof(ChatMessageDB.ThreadId), threadId,
            NullLogger.Instance, CancellationToken.None);

        result.Should().Contain(msg.Id);
    }

    [Test]
    public async Task Skips_NullableProperty_WhenNull()
    {
        var msg = CreateMessage(threadId: null);

        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        // Shard for ThreadId should not contain any entries
        var shardPath = ColdEntityIndex.GetShardPath(_fs, _entityDir, "ThreadId");
        if (File.Exists(shardPath))
        {
            var json = await File.ReadAllTextAsync(shardPath);
            // Should be empty or have no keys
            json.Should().NotContain(msg.Id.ToString());
        }
    }

    // ── Update (re-index on modify) ───────────────────────────────

    [Test]
    public async Task Update_MovesEntityToNewKey()
    {
        var channel1 = Guid.NewGuid();
        var channel2 = Guid.NewGuid();
        var msg = CreateMessage(channelId: channel1);

        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        // Simulate channel change
        msg.ChannelId = channel2;
        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        var old = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, nameof(ChatMessageDB.ChannelId), channel1,
            NullLogger.Instance, CancellationToken.None);
        var updated = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, nameof(ChatMessageDB.ChannelId), channel2,
            NullLogger.Instance, CancellationToken.None);

        old.Should().BeNull("old key should be pruned");
        updated.Should().Contain(msg.Id);
    }

    // ── Unknown entity type is a no-op ────────────────────────────

    [Test]
    public async Task UnknownEntityType_IsNoOp()
    {
        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            _entityDir, "UnknownDB", Guid.NewGuid(), new object(),
            deleted: false, NullLogger.Instance, CancellationToken.None);

        // No shard files should be created
        var indexFiles = Directory.GetFiles(_entityDir, "_index_*.json");
        indexFiles.Should().BeEmpty();
    }

    // ── RebuildIndexAsync ─────────────────────────────────────────

    [Test]
    public async Task RebuildIndexAsync_RebuildsFromEncryptedFiles()
    {
        var channelId = Guid.NewGuid();
        var msg1 = CreateMessage(channelId: channelId);
        var msg2 = CreateMessage(channelId: channelId);

        // Write encrypted entity files (no index)
        foreach (var msg in new[] { msg1, msg2 })
        {
            var json = JsonSerializer.Serialize(msg, ColdEntityStore.JsonOptions);
            var path = Path.Combine(_entityDir, $"{msg.Id}.json");
            await JsonFileEncryption.WriteJsonAsync(_fs, path, json, _key, encrypt: true, fsync: false, CancellationToken.None);
        }

        await ColdEntityIndex.RebuildIndexAsync(_fs,
            _entityDir, nameof(ChatMessageDB), typeof(ChatMessageDB),
            _key, ColdEntityStore.JsonOptions, NullLogger.Instance, CancellationToken.None);

        var result = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, nameof(ChatMessageDB.ChannelId), channelId,
            NullLogger.Instance, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain(msg1.Id);
        result.Should().Contain(msg2.Id);
    }

    [Test]
    public async Task RebuildIndexAsync_IgnoresUnderscorePrefixedFiles()
    {
        var msg = CreateMessage();
        var json = JsonSerializer.Serialize(msg, ColdEntityStore.JsonOptions);
        await JsonFileEncryption.WriteJsonAsync(_fs, 
            Path.Combine(_entityDir, $"{msg.Id}.json"), json, _key, encrypt: true, fsync: false, CancellationToken.None);
        // Stale index from a previous run
        await File.WriteAllTextAsync(Path.Combine(_entityDir, "_old_index.json"), "garbage");

        await ColdEntityIndex.RebuildIndexAsync(_fs, 
            _entityDir, nameof(ChatMessageDB), typeof(ChatMessageDB),
            _key, ColdEntityStore.JsonOptions, NullLogger.Instance, CancellationToken.None);

        var result = await ColdEntityIndex.LookupAsync(_fs, 
            _entityDir, nameof(ChatMessageDB.ChannelId), msg.ChannelId,
            NullLogger.Instance, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(msg.Id);
    }

    // ── Phase D: Sharded index tests ──────────────────────────────

    [Test]
    public async Task ShardedIndex_CreatesPerPropertyFiles()
    {
        var msg = CreateMessage(channelId: Guid.NewGuid(), threadId: Guid.NewGuid());

        await ColdEntityIndex.UpdateIndexAsync(_fs,
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        File.Exists(ColdEntityIndex.GetShardPath(_fs, _entityDir, "ChannelId")).Should().BeTrue();
        File.Exists(ColdEntityIndex.GetShardPath(_fs, _entityDir, "ThreadId")).Should().BeTrue();
        // SenderAgentId is null on this message, so its shard is not written (no data)
        File.Exists(ColdEntityIndex.GetShardPath(_fs, _entityDir, "SenderAgentId")).Should().BeFalse();
    }

    [Test]
    public async Task ShardedIndex_PartialUpdate_OnlyRewritesDirtyShard()
    {
        var channelId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var msg = CreateMessage(channelId: channelId, threadId: threadId);

        await ColdEntityIndex.UpdateIndexAsync(_fs,
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        // Record modification time of ThreadId shard
        var threadShardPath = ColdEntityIndex.GetShardPath(_fs, _entityDir, "ThreadId");
        var threadShardBefore = await File.ReadAllTextAsync(threadShardPath);

        // Update only ChannelId (add second entity with different channel, same thread)
        var msg2 = CreateMessage(channelId: Guid.NewGuid(), threadId: threadId);
        await ColdEntityIndex.UpdateIndexAsync(_fs,
            _entityDir, nameof(ChatMessageDB), msg2.Id, msg2,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        // ThreadId shard should have changed (has new entry) but ChannelId shard too
        var channelResult = await ColdEntityIndex.LookupAsync(_fs,
            _entityDir, "ChannelId", channelId,
            NullLogger.Instance, CancellationToken.None);
        channelResult.Should().HaveCount(1).And.Contain(msg.Id);

        var threadResult = await ColdEntityIndex.LookupAsync(_fs,
            _entityDir, "ThreadId", threadId,
            NullLogger.Instance, CancellationToken.None);
        threadResult.Should().HaveCount(2);
    }

    [Test]
    public async Task RebuildIndex_RemovesLegacyIndexFile()
    {
        // Write a legacy _index.json
        var legacyPath = Path.Combine(_entityDir, "_index.json");
        await File.WriteAllTextAsync(legacyPath, """{"ChannelId:fake":["id1"]}""");

        // Write an entity file so rebuild has something to process
        var msg = CreateMessage();
        var json = JsonSerializer.Serialize(msg, ColdEntityStore.JsonOptions);
        await JsonFileEncryption.WriteJsonAsync(_fs,
            Path.Combine(_entityDir, $"{msg.Id}.json"), json, _key, encrypt: true, fsync: false, CancellationToken.None);

        await ColdEntityIndex.RebuildIndexAsync(_fs,
            _entityDir, nameof(ChatMessageDB), typeof(ChatMessageDB),
            _key, ColdEntityStore.JsonOptions, NullLogger.Instance, CancellationToken.None);

        // Legacy file should be deleted
        File.Exists(legacyPath).Should().BeFalse();
        // Sharded files should exist
        File.Exists(ColdEntityIndex.GetShardPath(_fs, _entityDir, "ChannelId")).Should().BeTrue();
    }

    // ── Phase D: .tmp file cleanup ────────────────────────────────

    [Test]
    public void CleanupTempFiles_RemovesOrphanTmpFiles()
    {
        File.WriteAllText(Path.Combine(_entityDir, "abc.tmp"), "leftover");
        File.WriteAllText(Path.Combine(_entityDir, "def.tmp"), "leftover");
        File.WriteAllText(Path.Combine(_entityDir, "real.json"), "keep");

        ColdEntityIndex.CleanupTempFiles(_fs, _entityDir, NullLogger.Instance);

        File.Exists(Path.Combine(_entityDir, "abc.tmp")).Should().BeFalse();
        File.Exists(Path.Combine(_entityDir, "def.tmp")).Should().BeFalse();
        File.Exists(Path.Combine(_entityDir, "real.json")).Should().BeTrue();
    }

    // ── Phase D: Corrupt index recovery ───────────────────────────

    [Test]
    public async Task Lookup_WithCorruptShard_ReturnsEmptyAndRecovers()
    {
        // Write corrupt shard
        var shardPath = ColdEntityIndex.GetShardPath(_fs, _entityDir, "ChannelId");
        await File.WriteAllTextAsync(shardPath, "NOT VALID JSON{{{");

        // Should not throw — returns null (no match)
        var result = await ColdEntityIndex.LookupAsync(_fs,
            _entityDir, "ChannelId", Guid.NewGuid(),
            NullLogger.Instance, CancellationToken.None);

        result.Should().BeNull();
    }

    [Test]
    public async Task UpdateIndex_WithCorruptShard_RebuildsFromScratch()
    {
        // Write corrupt shard
        var shardPath = ColdEntityIndex.GetShardPath(_fs, _entityDir, "ChannelId");
        await File.WriteAllTextAsync(shardPath, "NOT VALID JSON{{{");

        var channelId = Guid.NewGuid();
        var msg = CreateMessage(channelId: channelId);

        // Should recover gracefully — rebuilds from empty
        await ColdEntityIndex.UpdateIndexAsync(_fs,
            _entityDir, nameof(ChatMessageDB), msg.Id, msg,
            deleted: false, NullLogger.Instance, CancellationToken.None);

        var result = await ColdEntityIndex.LookupAsync(_fs,
            _entityDir, "ChannelId", channelId,
            NullLogger.Instance, CancellationToken.None);

        result.Should().Contain(msg.Id);
    }
}
