using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Persistence;

/// <summary>
/// Phase F tests: Quarantine with exponential backoff retry, auto-purge,
/// and <see cref="ReadResult{T}"/> discriminated return type.
/// </summary>
[TestFixture]
public class QuarantineServiceTests
{
    private string _dataDir = null!;
    private byte[] _key = null!;
    private IPersistenceFileSystem _fs = null!;

    [SetUp]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"quarantine_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _key = ApiKeyEncryptor.GenerateKey();
        _fs = new PhysicalPersistenceFileSystem();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private string EntityDir => Path.Combine(_dataDir, nameof(ChatMessageDB));

    private async Task<string> WriteEncryptedEntity(ChatMessageDB msg)
    {
        var dir = EntityDir;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(msg, ColdEntityStore.JsonOptions);
        var path = Path.Combine(dir, $"{msg.Id}.json");
        await JsonFileEncryption.WriteJsonAsync(
            _fs, path, json, _key, encrypt: true, fsync: false, CancellationToken.None);
        return path;
    }

    private static ChatMessageDB CreateMessage(Guid? id = null, string content = "hello")
    {
        return new ChatMessageDB
        {
            Id = id ?? Guid.NewGuid(),
            ChannelId = Guid.NewGuid(),
            Role = "user",
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ── RGAP-14: Corrupt file → quarantine after retries ──────────

    [Test]
    public async Task ReadBytesWithRetry_CorruptFile_QuarantinedAfterRetries()
    {
        var msg = CreateMessage();
        var dir = EntityDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{msg.Id}.json");

        // Write garbage that looks like an encrypted envelope (0x01 prefix)
        // but has invalid GCM data — causes DecryptBytes to fail.
        var corrupt = new byte[64];
        corrupt[0] = 0x01;
        await File.WriteAllBytesAsync(path, corrupt);

        var result = await QuarantineService.ReadBytesWithRetryAsync(
            _fs, path, dir, _key, NullLogger.Instance, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Outcome.Should().Be(QuarantineService.ReadBytesOutcome.Corrupted);

        // Original file should be gone (moved to quarantine).
        File.Exists(path).Should().BeFalse();

        // Quarantine directory should contain the file.
        var quarantineDir = Path.Combine(dir, QuarantineService.QuarantineDir);
        Directory.Exists(quarantineDir).Should().BeTrue();
        Directory.GetFiles(quarantineDir, "*.json").Should().ContainSingle();
    }

    // ── RGAP-14: Transient error → success on retry ───────────────

    [Test]
    public async Task ReadBytesWithRetry_TransientError_SucceedsOnRetry()
    {
        var msg = CreateMessage(content: "retry success");
        var path = await WriteEncryptedEntity(msg);
        var dir = EntityDir;

        // Use InMemoryFS to simulate transient failure on first read.
        var memFs = new InMemoryPersistenceFileSystem();
        var fileData = await File.ReadAllBytesAsync(path);
        var memPath = memFs.CombinePath(_dataDir, nameof(ChatMessageDB), $"{msg.Id}.json");
        memFs.CreateDirectory(memFs.CombinePath(_dataDir, nameof(ChatMessageDB)));
        await memFs.WriteAllBytesAsync(memPath, fileData);

        var callCount = 0;
        memFs.OnBeforeRead = _ =>
        {
            callCount++;
            if (callCount <= 1)
                throw new IOException("Transient lock");
            return Task.CompletedTask;
        };

        var result = await QuarantineService.ReadBytesWithRetryAsync(
            memFs, memPath, memFs.CombinePath(_dataDir, nameof(ChatMessageDB)),
            _key, NullLogger.Instance, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        callCount.Should().BeGreaterThanOrEqualTo(2);

        result.Dispose();
    }

    // ── Purge by age ──────────────────────────────────────────────

    [Test]
    public void PurgeExpiredQuarantineFiles_RemovesOldFiles()
    {
        var dir = EntityDir;
        var quarantineDir = Path.Combine(dir, QuarantineService.QuarantineDir);
        Directory.CreateDirectory(quarantineDir);

        // Create a "quarantined" file with an old timestamp (40 days ago).
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-40).ToString("yyyyMMdd_HHmmss_fff");
        var oldFile = Path.Combine(quarantineDir, $"{Guid.NewGuid()}_{oldTimestamp}.json");
        File.WriteAllText(oldFile, "corrupt");

        // Create a recent file (1 day ago).
        var recentTimestamp = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyyMMdd_HHmmss_fff");
        var recentFile = Path.Combine(quarantineDir, $"{Guid.NewGuid()}_{recentTimestamp}.json");
        File.WriteAllText(recentFile, "corrupt");

        var purged = QuarantineService.PurgeExpiredQuarantineFiles(
            _fs, _dataDir, maxAgeDays: 30, NullLogger.Instance, CancellationToken.None);

        purged.Should().Be(1);
        File.Exists(oldFile).Should().BeFalse();
        File.Exists(recentFile).Should().BeTrue();
    }

    // ── Purge disabled (maxAge=0) ─────────────────────────────────

    [Test]
    public void PurgeExpiredQuarantineFiles_DisabledWhenZero()
    {
        var dir = EntityDir;
        var quarantineDir = Path.Combine(dir, QuarantineService.QuarantineDir);
        Directory.CreateDirectory(quarantineDir);

        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-999).ToString("yyyyMMdd_HHmmss_fff");
        var file = Path.Combine(quarantineDir, $"{Guid.NewGuid()}_{oldTimestamp}.json");
        File.WriteAllText(file, "corrupt");

        var purged = QuarantineService.PurgeExpiredQuarantineFiles(
            _fs, _dataDir, maxAgeDays: 0, NullLogger.Instance, CancellationToken.None);

        purged.Should().Be(0);
        File.Exists(file).Should().BeTrue();
    }

    // ── App continues after quarantine ────────────────────────────

    [Test]
    public async Task FindAsync_ContinuesAfterQuarantine()
    {
        var good = CreateMessage(content: "good");
        var bad = CreateMessage();
        await WriteEncryptedEntity(good);

        // Write a corrupt file for 'bad'.
        var dir = EntityDir;
        var badPath = Path.Combine(dir, $"{bad.Id}.json");
        await File.WriteAllBytesAsync(badPath, Encoding.UTF8.GetBytes("NOT JSON AT ALL"));

        var fileOptions = new JsonFileOptions { DataDirectory = _dataDir, EncryptAtRest = true };
        var encOptions = new EncryptionOptions { Key = _key };
        var store = new ColdEntityStore(_fs, fileOptions, encOptions, NullLogger<ColdEntityStore>.Instance);

        // Good entity loads fine.
        var goodResult = await store.FindAsync<ChatMessageDB>(good.Id);
        goodResult.IsSuccess.Should().BeTrue();
        goodResult.ValueOrDefault!.Content.Should().Be("good");

        // Bad entity returns Corrupted and gets quarantined.
        var badResult = await store.FindAsync<ChatMessageDB>(bad.Id);
        badResult.Should().BeOfType<ReadResult<ChatMessageDB>.Corrupted>();

        // Original bad file should be gone.
        File.Exists(badPath).Should().BeFalse();
    }

    // ── ReadResult discrimination ─────────────────────────────────

    [Test]
    public void ReadResult_Discrimination()
    {
        var entity = CreateMessage(content: "test");

        ReadResult<ChatMessageDB> success = new ReadResult<ChatMessageDB>.Success(entity);
        success.IsSuccess.Should().BeTrue();
        success.ValueOrDefault.Should().BeSameAs(entity);

        ReadResult<ChatMessageDB> notFound = new ReadResult<ChatMessageDB>.NotFound();
        notFound.IsSuccess.Should().BeFalse();
        notFound.ValueOrDefault.Should().BeNull();

        ReadResult<ChatMessageDB> corrupted = new ReadResult<ChatMessageDB>.Corrupted(
            new InvalidOperationException("bad"), "/some/path.json");
        corrupted.IsSuccess.Should().BeFalse();
        corrupted.ValueOrDefault.Should().BeNull();
        (corrupted as ReadResult<ChatMessageDB>.Corrupted)!.FilePath.Should().Be("/some/path.json");

        ReadResult<ChatMessageDB> ioError = new ReadResult<ChatMessageDB>.IoError(
            new IOException("lock"), "/some/path.json");
        ioError.IsSuccess.Should().BeFalse();
        (ioError as ReadResult<ChatMessageDB>.IoError)!.Exception.Should().BeOfType<IOException>();
    }

    // ── Timestamp parsing ─────────────────────────────────────────

    [Test]
    public void TryParseQuarantineTimestamp_ValidAndInvalid()
    {
        var ts = DateTimeOffset.UtcNow;
        var formatted = ts.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"{Guid.NewGuid()}_{formatted}.json";

        QuarantineService.TryParseQuarantineTimestamp(fileName, out var parsed)
            .Should().BeTrue();
        parsed.Year.Should().Be(ts.Year);
        parsed.Month.Should().Be(ts.Month);
        parsed.Day.Should().Be(ts.Day);

        QuarantineService.TryParseQuarantineTimestamp("short.json", out _)
            .Should().BeFalse();
    }
}
