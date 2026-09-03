using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Tests.Kernel;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public sealed class CanonicalJobsStoreTests
{
    [Test]
    public async Task CanonicalJobsStore_PersistsThroughAtomicGatewayAndReopens()
    {
        var dataDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "jsoncoldstore-canonical-jobs-" + Guid.NewGuid().ToString("N"));
        var job = CreateJob();

        try
        {
            await using (var db = CreateDbContext(dataDirectory))
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
                var store = new KernelJobsStore(CreateGateway(db));

                await store.SaveJobAsync(job);
            }

            await using (var db = CreateDbContext(dataDirectory))
            {
                var store = new KernelJobsStore(CreateGateway(db));
                var recovered = await store.GetJobAsync(job.Id);

                recovered.Should().NotBeNull();
                recovered!.Value.Should().BeEquivalentTo(job);
                recovered.Revision.Should().BeGreaterThan(0);
            }
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static JobDocument CreateJob()
    {
        var now = DateTimeOffset.UtcNow;
        return new JobDocument(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            new SharpClawActionKey("jobs.submit"),
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            JobStatus.Pending,
            [],
            now,
            null,
            null,
            null,
            ActionOutcomeCertainty.Certain,
            new JobPayloadEnvelope("test.job", 1, "{}"));
    }

    private static SharpClawDbContext CreateDbContext(string dataDirectory)
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
        return new SharpClawDbContext(
            options,
            new TestPersistenceActionRunnerAccessor(
                new RuntimePersistenceActionRunner(new TestPersistenceActionBoundary())));
    }

    private static BundledModuleStorageGateway CreateGateway(SharpClawDbContext db) =>
        new(
            db,
            CanonicalJobsStorageContractProvider.Instance,
            new TestRuntimeTransactionActionRunnerAccessor(
                new RuntimeTransactionActionRunner(
                    db,
                    new TestRuntimeTransactionActionBoundary())));

    private sealed class CanonicalJobsStorageContractProvider : IModuleStorageContractProvider
    {
        public static readonly CanonicalJobsStorageContractProvider Instance = new();

        public IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts() =>
            KernelJobsStorage.Contracts;

        public ModuleStorageContractDescriptor? FindStorageContract(
            string moduleId,
            string storageName) =>
            KernelJobsStorage.Contracts.FirstOrDefault(contract =>
                contract.ModuleId == moduleId && contract.StorageName == storageName);
    }

    private sealed class TestRuntimeTransactionActionBoundary : IRuntimeTransactionActionBoundary
    {
        public ValueTask<RuntimeTransactionActionResult> RunTransactionActionAsync(
            RuntimeTransactionActionInvocation invocation,
            Func<CancellationToken, ValueTask<RuntimeTransactionActionResult>> terminal,
            CancellationToken cancellationToken = default) =>
            terminal(cancellationToken);
    }

    private sealed class TestPersistenceActionBoundary : IRuntimePersistenceActionBoundary
    {
        public async ValueTask RunPersistenceActionAsync(
            RuntimePersistenceActionInvocation invocation,
            Func<CancellationToken, ValueTask<int>> terminal,
            CancellationToken cancellationToken = default)
        {
            _ = await terminal(cancellationToken);
        }
    }

    private sealed class TestPersistenceActionRunnerAccessor(
        RuntimePersistenceActionRunner runner) : IRuntimePersistenceActionRunnerAccessor
    {
        public RuntimePersistenceActionRunner GetRequiredRunner() => runner;
    }
}
