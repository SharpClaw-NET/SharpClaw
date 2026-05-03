using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public class JsonFilePersistenceServiceTests
{
    private string _dataDir = null!;
    private IPersistenceFileSystem _fs = null!;
    private EncryptionOptions _encryptionOptions = null!;
    private JsonFileOptions _jsonOptions = null!;
    private ServiceProvider _services = null!;
    private SharpClawDbContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"json_persistence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _fs = new PhysicalPersistenceFileSystem();
        _encryptionOptions = new EncryptionOptions { Key = ApiKeyEncryptor.GenerateKey() };
        _jsonOptions = new JsonFileOptions
        {
            DataDirectory = _dataDir,
            EncryptAtRest = true,
            FsyncOnWrite = false,
            AsyncFlush = false,
            EnableChecksums = false,
            EnableSnapshots = false,
        };

        var services = new ServiceCollection();
        services.AddSingleton(_jsonOptions);
        services.AddSingleton(_encryptionOptions);
        services.AddSingleton(_fs);
        services.AddSingleton<IPersistenceFileSystem>(_fs);
        services.AddSingleton<DirectoryLockManager>();
        services.AddSingleton<TransactionQueue>();
        services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        _services = services.BuildServiceProvider();
        _context = _services.GetRequiredService<SharpClawDbContext>();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _services.Dispose();

        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    [Test]
    public async Task LoadAsync_QuarantinesUnreadableEncryptedEntityFiles()
    {
        var entityDir = Path.Combine(_dataDir, nameof(ModuleStateDB));
        Directory.CreateDirectory(entityDir);

        var badFilePath = Path.Combine(entityDir, $"{Guid.NewGuid()}.json");
        var corruptEnvelope = new byte[64];
        corruptEnvelope[0] = 0x01;
        await File.WriteAllBytesAsync(badFilePath, corruptEnvelope);

        var sut = new JsonFilePersistenceService(
            _fs,
            _context,
            _jsonOptions,
            _encryptionOptions,
            NullLogger<JsonFilePersistenceService>.Instance);

        await sut.LoadAsync();

        File.Exists(badFilePath).Should().BeFalse();

        var quarantineDir = Path.Combine(entityDir, QuarantineService.QuarantineDir);
        Directory.Exists(quarantineDir).Should().BeTrue();
        Directory.GetFiles(quarantineDir, "*.json").Should().ContainSingle();
        _context.ModuleStates.Should().BeEmpty();
    }
}
