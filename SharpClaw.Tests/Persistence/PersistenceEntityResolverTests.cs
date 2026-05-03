using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Persistence;

/// <summary>
/// Proves provider neutrality for <see cref="IPersistenceEntityResolver"/>.
///
/// JSON/InMemory mode: entities absent from the hot EF set (cold entities
/// from a previous session) must be resolved transparently via
/// <see cref="JsonPersistenceEntityResolver"/>.
///
/// EF-only mode: the same query shapes succeed via
/// <see cref="EfPersistenceEntityResolver"/> without <see cref="ColdEntityStore"/>
/// being registered or referenced.
/// </summary>
[TestFixture]
public class PersistenceEntityResolverTests
{
    // ── Shared helpers ────────────────────────────────────────────

    private static SharpClawDbContext CreateDb()
        => new(new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static (ColdEntityStore store, JsonFileOptions options, string dataDir)
        CreateColdStore()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"resolver_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var fs = new PhysicalPersistenceFileSystem();
        var fileOptions = new JsonFileOptions
        {
            DataDirectory = dataDir,
            EncryptAtRest = false,
            FsyncOnWrite = false,
            EnableChecksums = false,
            EnableSnapshots = false,
        };
        var encOptions = new EncryptionOptions { Key = ApiKeyEncryptor.GenerateKey() };
        var store = new ColdEntityStore(
            fs, fileOptions, encOptions, NullLogger<ColdEntityStore>.Instance);
        return (store, fileOptions, dataDir);
    }

    /// <summary>
    /// Writes an entity directly to the cold storage directory, bypassing EF,
    /// so that a fresh EF context knows nothing about it — simulating a
    /// cross-session gap.
    /// </summary>
    private static async Task WriteColdAsync<T>(
        JsonFileOptions options, T entity, CancellationToken ct = default)
        where T : BaseEntity
    {
        var dir = Path.Combine(options.DataDirectory, typeof(T).Name);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(entity, ColdEntityStore.JsonOptions);
        var path = Path.Combine(dir, $"{entity.Id}.json");
        var fs = new PhysicalPersistenceFileSystem();
        // EncryptAtRest = false in test options — key is not used.
        await JsonFileEncryption.WriteJsonAsync(
            fs, path, json, key: [], encrypt: false, fsync: false, ct);
    }

    private static TaskInstanceDB MakeInstance(Guid? defId = null) => new()
    {
        Id = Guid.NewGuid(),
        TaskDefinitionId = defId ?? Guid.NewGuid(),
        Status = TaskInstanceStatus.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ChatMessageDB MakeMessage(Guid channelId, Guid? threadId = null) => new()
    {
        Id = Guid.NewGuid(),
        ChannelId = channelId,
        ThreadId = threadId,
        Role = "user",
        Content = "hello",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static AgentJobDB MakeJob(Guid channelId) => new()
    {
        Id = Guid.NewGuid(),
        ChannelId = channelId,
        AgentId = Guid.NewGuid(),
        Status = AgentJobStatus.Queued,
        ActionKey = "test_action",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ════════════════════════════════════════════════════════════════
    // EF-only resolver (relational provider path)
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task EfResolver_FindAsync_ReturnsEntityFromEfSet()
    {
        using var db = CreateDb();
        var instance = MakeInstance();
        db.TaskInstances.Add(instance);
        await db.SaveChangesAsync();

        var sut = new EfPersistenceEntityResolver();

        var result = await sut.FindAsync<TaskInstanceDB>(db, instance.Id);

        result.Should().NotBeNull();
        result!.Id.Should().Be(instance.Id);
    }

    [Test]
    public async Task EfResolver_FindAsync_ReturnsNullWhenNotFound()
    {
        using var db = CreateDb();
        var sut = new EfPersistenceEntityResolver();

        var result = await sut.FindAsync<TaskInstanceDB>(db, Guid.NewGuid());

        result.Should().BeNull();
    }

    [Test]
    public async Task EfResolver_QueryAsync_ReturnsMatchingEntities()
    {
        var channelId = Guid.NewGuid();
        using var db = CreateDb();
        db.ChatMessages.AddRange(MakeMessage(channelId), MakeMessage(channelId), MakeMessage(Guid.NewGuid()));
        await db.SaveChangesAsync();

        var sut = new EfPersistenceEntityResolver();

        var results = await sut.QueryAsync<ChatMessageDB>(
            db, m => m.ChannelId == channelId);

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(m => m.ChannelId.Should().Be(channelId));
    }

    [Test]
    public async Task EfResolver_QueryAsync_WithLimit_ReturnsUpToLimitEntities()
    {
        var channelId = Guid.NewGuid();
        using var db = CreateDb();
        for (var i = 0; i < 5; i++)
            db.ChatMessages.Add(MakeMessage(channelId));
        await db.SaveChangesAsync();

        var sut = new EfPersistenceEntityResolver();

        var results = await sut.QueryAsync<ChatMessageDB>(
            db, m => m.ChannelId == channelId, limit: 3);

        results.Should().HaveCount(3);
    }

    [Test]
    public async Task EfResolver_DoesNotRequireColdEntityStore()
    {
        // Verifies that EfPersistenceEntityResolver can be constructed and
        // used without ColdEntityStore being registered anywhere.
        using var db = CreateDb();
        var job = MakeJob(Guid.NewGuid());
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();

        // No ColdEntityStore instantiated — just the EF resolver.
        var sut = new EfPersistenceEntityResolver();

        var result = await sut.FindAsync<AgentJobDB>(db, job.Id);

        result.Should().NotBeNull();
        result!.Id.Should().Be(job.Id);
    }

    // ════════════════════════════════════════════════════════════════
    // JSON resolver — cold entity fallback (JSON/InMemory mode)
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task JsonResolver_FindAsync_ReturnsHotEntityFromEf_WhenPresent()
    {
        var (store, options, dataDir) = CreateColdStore();
        try
        {
            using var db = CreateDb();
            var instance = MakeInstance();
            db.TaskInstances.Add(instance);
            await db.SaveChangesAsync();

            var sut = new JsonPersistenceEntityResolver(options, store);

            var result = await sut.FindAsync<TaskInstanceDB>(db, instance.Id);

            result.Should().NotBeNull();
            result!.Id.Should().Be(instance.Id);
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }

    [Test]
    public async Task JsonResolver_FindAsync_HydratesTaskInstanceFromColdStorage_WhenAbsentFromEf()
    {
        // Phase 0/8: simulate cross-session gap — entity written to cold storage
        // in a "previous session", fresh EF context knows nothing about it.
        var (store, options, dataDir) = CreateColdStore();
        try
        {
            var instance = MakeInstance();
            await WriteColdAsync(options, instance);

            // New session: empty hot EF context.
            using var db = CreateDb();
            var sut = new JsonPersistenceEntityResolver(options, store);

            var result = await sut.FindAsync<TaskInstanceDB>(db, instance.Id);

            result.Should().NotBeNull();
            result!.Id.Should().Be(instance.Id);
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }

    [Test]
    public async Task JsonResolver_FindAsync_ReturnsNull_ForNonColdEntityType_WhenAbsentFromEf()
    {
        var (store, options, dataDir) = CreateColdStore();
        try
        {
            // TaskDefinitionDB is NOT in ColdEntityTypes — resolver must not
            // attempt a cold fallback and must return null.
            using var db = CreateDb();
            var sut = new JsonPersistenceEntityResolver(options, store);

            var result = await sut.FindAsync<TaskDefinitionDB>(db, Guid.NewGuid());

            result.Should().BeNull();
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }

    [Test]
    public async Task JsonResolver_FindAsync_AttachesColdEntityToContext_SoEntityIsTracked()
    {
        var (store, options, dataDir) = CreateColdStore();
        try
        {
            var instance = MakeInstance();
            await WriteColdAsync(options, instance);

            using var db = CreateDb();
            var sut = new JsonPersistenceEntityResolver(options, store);

            var result = await sut.FindAsync<TaskInstanceDB>(db, instance.Id);

            // Entity is tracked by the context after hydration — mutations are
            // visible to change tracking and SaveChangesAsync in a real session
            // where the entity exists in the durable store.
            result.Should().NotBeNull();
            db.Entry(result!).State.Should().NotBe(Microsoft.EntityFrameworkCore.EntityState.Detached);
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }

    [Test]
    public async Task JsonResolver_QueryAsync_MergesColdAndHotEntities_Deduplicates()
    {
        var channelId = Guid.NewGuid();
        var (store, options, dataDir) = CreateColdStore();
        try
        {
            // One message written to cold, one in hot EF set.
            var coldMsg = MakeMessage(channelId);
            await WriteColdAsync(options, coldMsg);

            using var db = CreateDb();
            var hotMsg = MakeMessage(channelId);
            db.ChatMessages.Add(hotMsg);
            await db.SaveChangesAsync();

            var sut = new JsonPersistenceEntityResolver(options, store);

            var results = await sut.QueryAsync<ChatMessageDB>(
                db,
                m => m.ChannelId == channelId,
                new PersistenceQueryHint("ChannelId", channelId));

            results.Should().HaveCount(2);
            results.Select(m => m.Id).Should().Contain(coldMsg.Id);
            results.Select(m => m.Id).Should().Contain(hotMsg.Id);
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }

    [Test]
    public async Task JsonResolver_QueryAsync_DoesNotDuplicateEntityPresentInBothHotAndCold()
    {
        var channelId = Guid.NewGuid();
        var (store, options, dataDir) = CreateColdStore();
        try
        {
            var msg = MakeMessage(channelId);

            // Write to cold first, then load into hot EF set — simulates
            // an entity that was loaded from cold and then re-attached.
            await WriteColdAsync(options, msg);
            using var db = CreateDb();
            db.ChatMessages.Add(msg);
            await db.SaveChangesAsync();

            var sut = new JsonPersistenceEntityResolver(options, store);

            var results = await sut.QueryAsync<ChatMessageDB>(
                db,
                m => m.ChannelId == channelId,
                new PersistenceQueryHint("ChannelId", channelId));

            results.Should().HaveCount(1);
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }

    [Test]
    public async Task JsonResolver_QueryAsync_WithLimit_ReturnsUpToLimitAcrossHotAndCold()
    {
        var channelId = Guid.NewGuid();
        var (store, options, dataDir) = CreateColdStore();
        try
        {
            // 3 cold messages
            for (var i = 0; i < 3; i++)
                await WriteColdAsync(options, MakeMessage(channelId));

            // 3 hot messages
            using var db = CreateDb();
            for (var i = 0; i < 3; i++)
                db.ChatMessages.Add(MakeMessage(channelId));
            await db.SaveChangesAsync();

            var sut = new JsonPersistenceEntityResolver(options, store);

            var results = await sut.QueryAsync<ChatMessageDB>(
                db,
                m => m.ChannelId == channelId,
                limit: 4,
                new PersistenceQueryHint("ChannelId", channelId));

            results.Should().HaveCount(4);
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }

    [Test]
    public async Task JsonResolver_FindAsync_AgentJob_HydratedFromColdStorage()
    {
        var (store, options, dataDir) = CreateColdStore();
        try
        {
            var job = MakeJob(Guid.NewGuid());
            await WriteColdAsync(options, job);

            using var db = CreateDb();
            var sut = new JsonPersistenceEntityResolver(options, store);

            var result = await sut.FindAsync<AgentJobDB>(db, job.Id);

            result.Should().NotBeNull();
            result!.Id.Should().Be(job.Id);
            result.ActionKey.Should().Be("test_action");
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }

    [Test]
    public async Task JsonResolver_QueryAsync_TaskInstance_ByDefinitionId_FindsColdInstance()
    {
        var defId = Guid.NewGuid();
        var (store, options, dataDir) = CreateColdStore();
        try
        {
            var instance = MakeInstance(defId);
            await WriteColdAsync(options, instance);

            using var db = CreateDb();
            var sut = new JsonPersistenceEntityResolver(options, store);

            var results = await sut.QueryAsync<TaskInstanceDB>(
                db,
                i => i.TaskDefinitionId == defId,
                new PersistenceQueryHint("TaskDefinitionId", defId));

            results.Should().HaveCount(1);
            results[0].Id.Should().Be(instance.Id);
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }
}
