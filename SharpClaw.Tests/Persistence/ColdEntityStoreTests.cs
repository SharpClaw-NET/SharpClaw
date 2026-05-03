using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public class ColdEntityStoreTests
{
    private string _dataDir = null!;
    private byte[] _key = null!;
    private ColdEntityStore _store = null!;
    private IPersistenceFileSystem _fs = null!;

    [SetUp]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"cold_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _key = ApiKeyEncryptor.GenerateKey();
        _fs = new PhysicalPersistenceFileSystem();

        var fileOptions = new JsonFileOptions { DataDirectory = _dataDir, EncryptAtRest = true };
        var encOptions = new EncryptionOptions { Key = _key };
        _store = new ColdEntityStore(_fs, fileOptions, encOptions, NullLogger<ColdEntityStore>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private async Task WriteEncryptedEntity(ChatMessageDB msg)
    {
        var dir = Path.Combine(_dataDir, nameof(ChatMessageDB));
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(msg, ColdEntityStore.JsonOptions);
        var path = Path.Combine(dir, $"{msg.Id}.json");
        await JsonFileEncryption.WriteJsonAsync(_fs, path, json, _key, encrypt: true, fsync: false, CancellationToken.None);
    }

    private static ChatMessageDB CreateMessage(
        Guid? id = null, Guid? channelId = null, Guid? threadId = null, string content = "hello")
    {
        return new ChatMessageDB
        {
            Id = id ?? Guid.NewGuid(),
            ChannelId = channelId ?? Guid.NewGuid(),
            ThreadId = threadId,
            Role = "user",
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ── FindAsync ─────────────────────────────────────────────────

    [Test]
    public async Task FindAsync_ReturnsSuccess_WhenEncryptedFileExists()
    {
        var msg = CreateMessage(content: "find me");
        await WriteEncryptedEntity(msg);

        var result = await _store.FindAsync<ChatMessageDB>(msg.Id);

        result.Should().BeOfType<ReadResult<ChatMessageDB>.Success>();
        result.ValueOrDefault!.Content.Should().Be("find me");
        result.ValueOrDefault.Id.Should().Be(msg.Id);
    }

    [Test]
    public async Task FindAsync_ReturnsNotFound_WhenMissing()
    {
        var result = await _store.FindAsync<ChatMessageDB>(Guid.NewGuid());
        result.Should().BeOfType<ReadResult<ChatMessageDB>.NotFound>();
        result.ValueOrDefault.Should().BeNull();
    }

    // ── QueryAsync ────────────────────────────────────────────────

    [Test]
    public async Task QueryAsync_FiltersAndLimits()
    {
        var channelId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            var msg = CreateMessage(channelId: channelId, content: $"msg{i}");
            msg.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i);
            msg.UpdatedAt = msg.CreatedAt;
            await WriteEncryptedEntity(msg);
        }
        // Different channel
        await WriteEncryptedEntity(CreateMessage(content: "other"));

        var results = await _store.QueryAsync<ChatMessageDB>(
            m => m.ChannelId == channelId, limit: 3);

        results.Should().HaveCount(3);
        results.Should().AllSatisfy(m => m.ChannelId.Should().Be(channelId));
        // Should be chronological (re-sorted after limit)
        results.Should().BeInAscendingOrder(m => m.CreatedAt);
    }

    // ── QueryAllAsync ─────────────────────────────────────────────

    [Test]
    public async Task QueryAllAsync_ReturnsAllMatching()
    {
        var channelId = Guid.NewGuid();
        await WriteEncryptedEntity(CreateMessage(channelId: channelId, content: "a"));
        await WriteEncryptedEntity(CreateMessage(channelId: channelId, content: "b"));
        await WriteEncryptedEntity(CreateMessage(content: "other channel"));

        var results = await _store.QueryAllAsync<ChatMessageDB>(m => m.ChannelId == channelId);

        results.Should().HaveCount(2);
    }

    // ── QueryAsync with IndexFilter ───────────────────────────────

    [Test]
    public async Task QueryAsync_WithIndexFilter_UsesIndex()
    {
        var channelId = Guid.NewGuid();
        var dir = Path.Combine(_dataDir, nameof(ChatMessageDB));

        var msg1 = CreateMessage(channelId: channelId, content: "indexed");
        var msg2 = CreateMessage(content: "not indexed");
        await WriteEncryptedEntity(msg1);
        await WriteEncryptedEntity(msg2);

        // Build index for msg1
        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            dir, nameof(ChatMessageDB), msg1.Id, msg1, deleted: false,
            NullLogger.Instance, CancellationToken.None);
        await ColdEntityIndex.UpdateIndexAsync(_fs, 
            dir, nameof(ChatMessageDB), msg2.Id, msg2, deleted: false,
            NullLogger.Instance, CancellationToken.None);

        var filter = new ColdEntityStore.IndexFilter(nameof(ChatMessageDB.ChannelId), channelId);
        var results = await _store.QueryAsync<ChatMessageDB>(_ => true, limit: 100, indexFilter: filter);

        results.Should().ContainSingle().Which.Content.Should().Be("indexed");
    }

    // ── Plaintext file compatibility ──────────────────────────────

    [Test]
    public async Task FindAsync_ReadsPlaintextFile()
    {
        var msg = CreateMessage(content: "plaintext");
        var dir = Path.Combine(_dataDir, nameof(ChatMessageDB));
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(msg, ColdEntityStore.JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{msg.Id}.json"), json);

        var result = await _store.FindAsync<ChatMessageDB>(msg.Id);

        result.IsSuccess.Should().BeTrue();
        result.ValueOrDefault!.Content.Should().Be("plaintext");
    }

    // ── Underscore-prefixed files are skipped ─────────────────────

    [Test]
    public async Task QueryAllAsync_SkipsUnderscorePrefixedFiles()
    {
        var msg = CreateMessage(content: "real");
        await WriteEncryptedEntity(msg);

        // Write an _index.json file that would fail deserialization
        var dir = Path.Combine(_dataDir, nameof(ChatMessageDB));
        await File.WriteAllTextAsync(Path.Combine(dir, "_index.json"), "{}");

        var results = await _store.QueryAllAsync<ChatMessageDB>(_ => true);

        results.Should().ContainSingle().Which.Content.Should().Be("real");
    }
}
