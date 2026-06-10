using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SharpClaw.Application.API;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;

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
    public async Task Claim_PatchesMatchingRecordsAndReplacesClaimIndexes()
    {
        await using var db = CreateDbContext();
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
        db.ModuleStorageIndexEntries.Single(index =>
                index.RecordKey == "due" && index.IndexName == "status")
            .StringValue
            .Should()
            .Be("Running");
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
