using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Kernel;
using SharpClaw.Persistence;
using SharpClaw.Persistence.JSONColdStore;
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
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:EncryptDatabase"] = "false",
            })
            .Build();
        using var services = new ServiceCollection()
            .AddSingleton(new EncryptionOptions { Key = new byte[32] })
            .BuildServiceProvider();
        var persistenceOptions = new SharpClawPersistenceOptions
        {
            DataDirectory = dataDirectory,
        };
        var builder = new DbContextOptionsBuilder<SharpClawDbContext>();
        new JSONColdStorePersistenceProvider().Configure(
            builder,
            new SharpClawPersistenceProviderContext(
                services,
                configuration,
                persistenceOptions,
                typeof(SharpClawDbContext),
                UseMigrations: true));
        return new SharpClawDbContext(
            builder.Options,
            new RuntimePersistenceActionRunner(new TestPersistenceActionBoundary()));
    }

    private static ScopedStorageGateway CreateGateway(SharpClawDbContext db) =>
        new(
            db,
            CanonicalJobsStorageContractProvider.Instance,
            new TestRuntimeTransactionActionRunnerAccessor(
                new RuntimeTransactionActionRunner(
                    db,
                    new TestRuntimeTransactionActionBoundary())));

    private sealed class CanonicalJobsStorageContractProvider : IStorageContractProvider
    {
        public static readonly CanonicalJobsStorageContractProvider Instance = new();

        public IReadOnlyList<ScopedStorageContractDescriptor> GetStorageContracts() =>
            KernelJobsStorage.Contracts;

        public ScopedStorageContractDescriptor? FindStorageContract(
            string SourceId,
            string storageName) =>
            KernelJobsStorage.Contracts.FirstOrDefault(contract =>
                contract.SourceId == SourceId && contract.StorageName == storageName);
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

}
