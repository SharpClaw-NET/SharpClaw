using System.Text.Json;
using FluentAssertions;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ScopedDocumentStoreTests
{
    [Test]
    public async Task UpsertManyAsync_PerformanceShape_UsesSingleBatchGatewayCall()
    {
        var gateway = new RecordingStorageGateway();
        var store = new ScopedDocumentStore<SampleRecord>(
            gateway,
            "sample_registration",
            "jobs");

        var saved = await store.UpsertManyAsync(
            Enumerable.Range(0, 25)
                .Select(i => new ScopedDocumentWrite<SampleRecord>(
                    $"job-{i:D2}",
                    new SampleRecord("Pending", i),
                    new { status = "Pending", priority = i })));

        saved.Should().Be(25);
        gateway.Calls.Should().ContainSingle();
        gateway.Calls[0].Operation.Should().Be(ScopedStorageOperations.BatchUpsert);
        gateway.Calls[0].Parameters
            .GetProperty("records")
            .GetArrayLength()
            .Should()
            .Be(25);
    }

    [Test]
    public async Task QueryBuilder_SendsIndexFiltersOrderAndLimit()
    {
        var gateway = new RecordingStorageGateway();
        var store = new ScopedDocumentStore<SampleRecord>(
            gateway,
            "sample_registration",
            "jobs");

        var records = await store.Query()
            .WhereIndex("status").EqualTo("Pending")
            .WhereIndex("priority").GreaterThanOrEqual(10)
            .OrderByIndexDescending("priority")
            .Take(5)
            .ToListAsync();

        records.Should().ContainSingle()
            .Which.Should().Be(new SampleRecord("Pending", 42));
        gateway.Calls.Should().ContainSingle();
        var payload = gateway.Calls[0].Parameters;
        gateway.Calls[0].Operation.Should().Be(ScopedStorageOperations.Query);
        payload.GetProperty("filters").EnumerateArray()
            .Select(filter => filter.GetProperty("indexName").GetString())
            .Should()
            .Equal("status", "priority");
        payload.GetProperty("orderBy").GetProperty("indexName").GetString().Should().Be("priority");
        payload.GetProperty("orderBy").GetProperty("direction").GetString().Should().Be("desc");
        payload.GetProperty("limit").GetInt32().Should().Be(5);
    }

    [Test]
    public async Task ClaimBuilder_RequiresPatchBeforeExecution()
    {
        var gateway = new RecordingStorageGateway();
        var store = new ScopedDocumentStore<SampleRecord>(
            gateway,
            "sample_registration",
            "jobs");

        var act = async () => await store.Claim()
            .WhereIndex("status").EqualTo("Pending")
            .ToListAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires a patch*");
        gateway.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task ClaimBuilder_SendsAtomicClaimPayloadWithPatchAndIndexes()
    {
        var gateway = new RecordingStorageGateway();
        var store = new ScopedDocumentStore<SampleRecord>(
            gateway,
            "sample_registration",
            "jobs");

        var records = await store.Claim()
            .WhereIndex("status").EqualTo("Pending")
            .WhereIndex("priority").LessThanOrEqual(10)
            .OrderByIndex("priority")
            .Patch(
                new { status = "Running", claimedBy = "worker-1" },
                new { status = "Running" })
            .Take(1)
            .ToListAsync();

        records.Should().ContainSingle()
            .Which.Status.Should().Be("Running");
        gateway.Calls.Should().ContainSingle();
        var payload = gateway.Calls[0].Parameters;
        gateway.Calls[0].Operation.Should().Be(ScopedStorageOperations.Claim);
        payload.GetProperty("patch").GetProperty("status").GetString().Should().Be("Running");
        payload.GetProperty("patch").GetProperty("claimedBy").GetString().Should().Be("worker-1");
        payload.GetProperty("indexes").GetProperty("status").GetString().Should().Be("Running");
        payload.GetProperty("limit").GetInt32().Should().Be(1);
    }

    private sealed record SampleRecord(string Status, int Priority);

    private sealed class RecordingStorageGateway : IScopedStorageGateway
    {
        public List<(string SourceId, string StorageName, string Operation, JsonElement Parameters)> Calls { get; } = [];

        public IReadOnlyList<ScopedStorageContractDescriptor> ListContracts() => [];

        public Task<JsonElement> InvokeAsync(
            string SourceId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            Calls.Add((SourceId, storageName, operation, parameters.Clone()));
            var result = operation switch
            {
                ScopedStorageOperations.BatchUpsert => JsonSerializer.SerializeToElement(new
                {
                    saved = parameters.GetProperty("records").GetArrayLength(),
                }),
                ScopedStorageOperations.Claim => JsonSerializer.SerializeToElement(new
                {
                    records = new[]
                    {
                        new
                        {
                            key = "job-42",
                            value = new SampleRecord("Running", 42),
                        },
                    },
                }),
                _ => JsonSerializer.SerializeToElement(new
                {
                    records = new[]
                    {
                        new
                        {
                            key = "job-42",
                            value = new SampleRecord("Pending", 42),
                        },
                    },
                }),
            };

            return Task.FromResult(result);
        }
    }
}
