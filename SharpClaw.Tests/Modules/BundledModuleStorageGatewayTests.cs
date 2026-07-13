using System.Text.Json;
using FluentAssertions;
using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SharpClaw.Runtime.Host;
using SharpClaw.Contracts.Modules;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class BundledModuleStorageGatewayTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ListContracts_ExposesParentBackedModuleStorageOperations()
    {
        await using var db = CreateDbContext();
        var gateway = CreateGateway(db);

        var contracts = gateway.ListContracts();

        contracts.Should().ContainSingle();
        contracts.Single().Operations.Select(operation => operation.Name)
            .Should()
            .BeEquivalentTo("get", "upsert", "batchUpsert", "delete", "batchDelete", "list", "query", "claim");
    }

    [Test]
    public async Task UpsertAndGet_PersistsRecordAndTypedIndexRows()
    {
        await using var db = CreateDbContext();
        var gateway = CreateGateway(db);
        var dueAt = DateTimeOffset.Parse("2026-06-10T12:00:00Z");

        await InvokeAsync(gateway, "upsert", new
        {
            key = "alpha",
            value = new
            {
                name = "Alpha",
                count = 2,
            },
            indexes = new
            {
                name = "Alpha",
                dueAt,
                priority = 2,
                active = true,
            },
        });

        var result = await InvokeAsync(gateway, "get", new { key = "alpha" });

        result.GetProperty("found").GetBoolean().Should().BeTrue();
        result.GetProperty("value").GetProperty("name").GetString().Should().Be("Alpha");
        db.ModuleStorageRecords.Should().ContainSingle();
        db.ModuleStorageIndexEntries.Should().HaveCount(4);
        db.ModuleStorageIndexEntries.Single(index => index.IndexName == "dueAt")
            .DateTimeValue
            .Should()
            .Be(dueAt);
    }

    [Test]
    public async Task Query_UsesTypedDateIndexAndReturnsRecordsInIndexOrder()
    {
        await using var db = CreateDbContext();
        var gateway = CreateGateway(db);

        await UpsertJobAsync(gateway, "late", DateTimeOffset.Parse("2026-06-10T14:00:00Z"));
        await UpsertJobAsync(gateway, "early", DateTimeOffset.Parse("2026-06-10T10:00:00Z"));
        await UpsertJobAsync(gateway, "middle", DateTimeOffset.Parse("2026-06-10T12:00:00Z"));

        var result = await InvokeAsync(gateway, "query", new
        {
            filters = new object[]
            {
                new
                {
                    indexName = "nextRunAt",
                    @operator = "lessThanOrEqual",
                    value = DateTimeOffset.Parse("2026-06-10T12:00:00Z"),
                },
            },
            orderBy = new
            {
                indexName = "nextRunAt",
                direction = "asc",
            },
        });

        result.GetProperty("records")
            .EnumerateArray()
            .Select(record => record.GetProperty("key").GetString())
            .Should()
            .Equal("early", "middle");
    }

    [Test]
    public async Task Delete_RemovesRecordAndIndexRows()
    {
        await using var db = CreateDbContext();
        var gateway = CreateGateway(db);
        await UpsertJobAsync(gateway, "delete-me", DateTimeOffset.Parse("2026-06-10T10:00:00Z"));

        var result = await InvokeAsync(gateway, "delete", new { key = "delete-me" });

        result.GetProperty("deleted").GetBoolean().Should().BeTrue();
        db.ModuleStorageRecords.Should().BeEmpty();
        db.ModuleStorageIndexEntries.Should().BeEmpty();
    }

    [Test]
    public async Task BatchUpsert_PersistsRecordsWithOneGatewayOperation()
    {
        await using var db = CreateDbContext();
        var telemetry = new RecordingStorageTelemetry();
        var gateway = CreateGateway(db, telemetry);

        var result = await InvokeAsync(gateway, "batchUpsert", new
        {
            records = new[]
            {
                new
                {
                    key = "first",
                    value = new { name = "First", nextRunAt = DateTimeOffset.Parse("2026-06-10T10:00:00Z") },
                    indexes = new { name = "First", nextRunAt = DateTimeOffset.Parse("2026-06-10T10:00:00Z") },
                },
                new
                {
                    key = "second",
                    value = new { name = "Second", nextRunAt = DateTimeOffset.Parse("2026-06-10T11:00:00Z") },
                    indexes = new { name = "Second", nextRunAt = DateTimeOffset.Parse("2026-06-10T11:00:00Z") },
                },
            },
        });

        result.GetProperty("saved").GetInt32().Should().Be(2);
        db.ModuleStorageRecords.Should().HaveCount(2);
        telemetry.Events.Should().ContainSingle(e =>
            e.Operation == ModuleStorageOperations.BatchUpsert && e.Success);
    }

    [Test]
    public async Task BatchUpsert_RejectsOverContractBatchSizeBeforeWritingRecords()
    {
        await using var db = CreateDbContext();
        var gateway = CreateGateway(db);

        var act = async () => await InvokeAsync(gateway, "batchUpsert", new
        {
            records = Enumerable.Range(0, 11)
                .Select(i => new
                {
                    key = $"job-{i:D2}",
                    value = new { name = $"Job {i}" },
                    indexes = new { name = $"Job {i}" },
                })
                .ToArray(),
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*cannot exceed 10 records*");
        db.ModuleStorageRecords.Should().BeEmpty();
        db.ModuleStorageIndexEntries.Should().BeEmpty();
    }

    [Test]
    public async Task Upsert_RejectsDocumentsOverDeclaredStorageQuota()
    {
        await using var db = CreateDbContext();
        var gateway = CreateGateway(db);

        var act = async () => await InvokeAsync(gateway, "upsert", new
        {
            key = "huge",
            value = new { content = new string('x', 70_000) },
            indexes = new { name = "Huge" },
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*exceeds the declared 65536 byte limit*");
        db.ModuleStorageRecords.Should().BeEmpty();
        db.ModuleStorageIndexEntries.Should().BeEmpty();
    }

    [Test]
    public async Task BatchDelete_RemovesManyRecordsAndIndexesWithOneGatewayOperation()
    {
        await using var db = CreateDbContext();
        var gateway = CreateGateway(db);
        await UpsertJobAsync(gateway, "first", DateTimeOffset.Parse("2026-06-10T10:00:00Z"));
        await UpsertJobAsync(gateway, "second", DateTimeOffset.Parse("2026-06-10T11:00:00Z"));
        await UpsertJobAsync(gateway, "third", DateTimeOffset.Parse("2026-06-10T12:00:00Z"));
        var telemetry = new RecordingStorageTelemetry();
        gateway = CreateGateway(db, telemetry);

        var result = await InvokeAsync(gateway, "batchDelete", new
        {
            keys = new[] { "first", "second", "third" },
        });

        result.GetProperty("deleted").GetInt32().Should().Be(3);
        db.ModuleStorageRecords.Should().BeEmpty();
        db.ModuleStorageIndexEntries.Should().BeEmpty();
        telemetry.Events.Should().ContainSingle(e =>
            e.Operation == ModuleStorageOperations.BatchDelete && e.Success);
    }

    [Test]
    public async Task Claim_PatchesMatchingRecordsAndReplacesClaimIndexes()
    {
        var dataDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "jsoncoldstore-module-claim-replace-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var db = CreateJsonColdStoreDbContext(dataDirectory);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var gateway = CreateGateway(db);

            await UpsertJobAsync(
                gateway,
                "due",
                DateTimeOffset.Parse("2026-06-10T10:00:00Z"),
                status: "Pending");
            await UpsertJobAsync(
                gateway,
                "future",
                DateTimeOffset.Parse("2026-06-10T14:00:00Z"),
                status: "Pending");

            var result = await InvokeAsync(gateway, "claim", new
            {
                filters = new object[]
                {
                    new { indexName = "status", @operator = "equals", value = "Pending" },
                    new
                    {
                        indexName = "nextRunAt",
                        @operator = "lessThanOrEqual",
                        value = DateTimeOffset.Parse("2026-06-10T12:00:00Z"),
                    },
                },
                orderBy = new { indexName = "nextRunAt", direction = "asc" },
                limit = 10,
                patch = new
                {
                    status = "Running",
                    lastRunAt = DateTimeOffset.Parse("2026-06-10T12:00:00Z"),
                },
                indexes = new
                {
                    status = "Running",
                },
            });

            var record = result.GetProperty("records").EnumerateArray().Single();
            record.GetProperty("key").GetString().Should().Be("due");
            record.GetProperty("value").GetProperty("status").GetString().Should().Be("Running");

            db.ChangeTracker.Clear();

            var statusIndex = await db.ModuleStorageIndexEntries.SingleAsync(index =>
                index.RecordKey == "due" && index.IndexName == "status");
            statusIndex.StringValue.Should().Be("Running");
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Claim_PatchesMatchingRecordsWithJsonColdStoreTransaction()
    {
        var dataDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "jsoncoldstore-module-claim-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var db = CreateJsonColdStoreDbContext(dataDirectory);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var gateway = CreateGateway(db);

            await UpsertJobAsync(
                gateway,
                "due",
                DateTimeOffset.Parse("2026-06-10T10:00:00Z"),
                status: "Pending");
            await UpsertJobAsync(
                gateway,
                "future",
                DateTimeOffset.Parse("2026-06-10T14:00:00Z"),
                status: "Pending");

            var result = await InvokeAsync(gateway, "claim", new
            {
                filters = new object[]
                {
                    new { indexName = "status", @operator = "equals", value = "Pending" },
                    new
                    {
                        indexName = "nextRunAt",
                        @operator = "lessThanOrEqual",
                        value = DateTimeOffset.Parse("2026-06-10T12:00:00Z"),
                    },
                },
                orderBy = new { indexName = "nextRunAt", direction = "asc" },
                limit = 10,
                patch = new
                {
                    status = "Running",
                    lastRunAt = DateTimeOffset.Parse("2026-06-10T12:00:00Z"),
                },
                indexes = new
                {
                    status = "Running",
                },
            });

            var record = result.GetProperty("records").EnumerateArray().Single();
            record.GetProperty("key").GetString().Should().Be("due");
            record.GetProperty("value").GetProperty("status").GetString().Should().Be("Running");

            db.ChangeTracker.Clear();

            var storedDue = await db.ModuleStorageRecords.SingleAsync(record =>
                record.ModuleId == "test_module"
                && record.StorageName == "records"
                && record.RecordKey == "due");
            storedDue.ValueJson.Should().Contain("\"Running\"");

            var storedFuture = await db.ModuleStorageRecords.SingleAsync(record =>
                record.ModuleId == "test_module"
                && record.StorageName == "records"
                && record.RecordKey == "future");
            storedFuture.ValueJson.Should().Contain("\"Pending\"");

            var dueStatus = await db.ModuleStorageIndexEntries.SingleAsync(index =>
                index.ModuleId == "test_module"
                && index.StorageName == "records"
                && index.RecordKey == "due"
                && index.IndexName == "status");
            dueStatus.StringValue.Should().Be("Running");
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Claim_RejectsIndexedFieldPatchWithoutReplacementIndex()
    {
        var dataDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "jsoncoldstore-module-claim-reject-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var db = CreateJsonColdStoreDbContext(dataDirectory);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var gateway = CreateGateway(db);
            await UpsertJobAsync(
                gateway,
                "due",
                DateTimeOffset.Parse("2026-06-10T10:00:00Z"),
                status: "Pending");

            var act = async () => await InvokeAsync(gateway, "claim", new
            {
                filters = new object[]
                {
                    new { indexName = "status", @operator = "equals", value = "Pending" },
                },
                patch = new
                {
                    status = "Running",
                },
            });

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*changes indexed field 'status' without replacing that index value*");

            db.ChangeTracker.Clear();

            var record = await db.ModuleStorageRecords.SingleAsync();
            record.ValueJson.Should().Contain("\"Pending\"");
            var statusIndex = await db.ModuleStorageIndexEntries.SingleAsync(index =>
                index.RecordKey == "due" && index.IndexName == "status");
            statusIndex.StringValue.Should().Be("Pending");
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Query_PerformanceShape_ReturnsLimitedIndexOrderedPageFromLargeStore()
    {
        await using var db = CreateDbContext();
        var gateway = CreateGateway(db);
        var baseTime = DateTimeOffset.Parse("2026-06-10T00:00:00Z");

        for (var batch = 0; batch < 100; batch++)
        {
            await InvokeAsync(gateway, "batchUpsert", new
            {
                records = Enumerable.Range(batch * 10, 10)
                    .Select(i => new
                    {
                        key = $"job-{i:D4}",
                        value = new
                        {
                            key = $"job-{i:D4}",
                            status = i % 2 == 0 ? "Pending" : "Done",
                            nextRunAt = baseTime.AddMinutes(i),
                        },
                        indexes = new
                        {
                            status = i % 2 == 0 ? "Pending" : "Done",
                            nextRunAt = baseTime.AddMinutes(i),
                        },
                    })
                    .ToArray(),
            });
        }

        var result = await InvokeAsync(gateway, "query", new
        {
            filters = new object[]
            {
                new { indexName = "status", @operator = "equals", value = "Pending" },
                new
                {
                    indexName = "nextRunAt",
                    @operator = "lessThanOrEqual",
                    value = baseTime.AddMinutes(20),
                },
            },
            orderBy = new
            {
                indexName = "nextRunAt",
                direction = "desc",
            },
            limit = 5,
        });

        result.GetProperty("records")
            .EnumerateArray()
            .Select(record => record.GetProperty("key").GetString())
            .Should()
            .Equal("job-0020", "job-0018", "job-0016", "job-0014", "job-0012");
    }

    [Test]
    public async Task Upsert_RejectsUndeclaredIndex()
    {
        await using var db = CreateDbContext();
        var gateway = CreateGateway(db);

        var act = async () => await InvokeAsync(gateway, "upsert", new
        {
            key = "bad",
            value = new { name = "Bad" },
            indexes = new { unknown = "Bad" },
        });

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static async Task UpsertJobAsync(
        BundledModuleStorageGateway gateway,
        string key,
        DateTimeOffset nextRunAt,
        string status = "Pending")
    {
        await InvokeAsync(gateway, "upsert", new
        {
            key,
            value = new
            {
                key,
                status,
                nextRunAt,
            },
            indexes = new
            {
                status,
                nextRunAt,
            },
        });
    }

    private static BundledModuleStorageGateway CreateGateway(
        SharpClawDbContext db,
        IModuleStorageTelemetry? telemetry = null) =>
        new(db, TestStorageContractProvider.Instance, telemetry);

    private static Task<JsonElement> InvokeAsync(
        BundledModuleStorageGateway gateway,
        string operation,
        object parameters) =>
        gateway.InvokeAsync(
            "test_module",
            "records",
            operation,
            JsonSerializer.SerializeToElement(parameters, JsonOptions));

    private static SharpClawDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase($"ModuleStorage_{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
            .Options;
        return new SharpClawDbContext(options);
    }

    private static SharpClawDbContext CreateJsonColdStoreDbContext(string dataDirectory)
    {
        var storageOptions = new JsonColdStoreStorageOptions
        {
            DataDirectory = dataDirectory,
            EncryptAtRest = false,
        };

        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseJsonColdStoreDatabase(
                storageOptions.DataDirectory,
                store => JsonColdStoreRegistration.ConfigureStore(store, storageOptions, null))
            .Options;

        return new SharpClawDbContext(options);
    }

    private sealed class TestStorageContractProvider : IModuleStorageContractProvider
    {
        public static readonly TestStorageContractProvider Instance = new();

        private static readonly IReadOnlyList<ModuleStorageContractDescriptor> Contracts =
        [
            new(
                "test_module",
                "records",
                [
                    new(ModuleStorageOperations.Get),
                    new(ModuleStorageOperations.Upsert),
                    new(ModuleStorageOperations.BatchUpsert),
                    new(ModuleStorageOperations.Delete),
                    new(ModuleStorageOperations.BatchDelete),
                    new(ModuleStorageOperations.List),
                    new(ModuleStorageOperations.Query),
                    new(ModuleStorageOperations.Claim),
                ],
                Indexes:
                [
                    new("name", ModuleStorageIndexValueKind.String),
                    new("dueAt", ModuleStorageIndexValueKind.DateTime, AllowsRange: true),
                    new("nextRunAt", ModuleStorageIndexValueKind.DateTime, AllowsRange: true),
                    new("priority", ModuleStorageIndexValueKind.Number, AllowsRange: true),
                    new("active", ModuleStorageIndexValueKind.Bool),
                    new("status", ModuleStorageIndexValueKind.String),
                ],
                MaxDocumentBytes: 65_536,
                MaxBatchSize: 10),
        ];

        public IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts() => Contracts;

        public ModuleStorageContractDescriptor? FindStorageContract(
            string moduleId,
            string storageName) =>
            Contracts.FirstOrDefault(contract =>
                contract.ModuleId == moduleId && contract.StorageName == storageName);
    }

    private sealed class RecordingStorageTelemetry : IModuleStorageTelemetry
    {
        public List<ModuleStorageTelemetryEvent> Events { get; } = [];

        public void Record(ModuleStorageTelemetryEvent telemetryEvent) =>
            Events.Add(telemetryEvent);
    }
}
