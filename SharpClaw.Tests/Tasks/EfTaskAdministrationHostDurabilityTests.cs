using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.INF.DurableStorage;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.DurableStorage;

namespace SharpClaw.Tests.Tasks;

[TestFixture]
public sealed class EfTaskAdministrationHostDurabilityTests
{
    [TestCase(TaskInstanceStatus.Completed, true)]
    [TestCase(TaskInstanceStatus.Failed, true)]
    [TestCase(TaskInstanceStatus.Cancelled, true)]
    [TestCase(TaskInstanceStatus.Completed, false)]
    [TestCase(TaskInstanceStatus.Failed, false)]
    [TestCase(TaskInstanceStatus.Cancelled, false)]
    public async Task TerminalPersistence_SealsDiagnosticsBeforeSaveChanges(
        TaskInstanceStatus terminalStatus,
        bool persistThroughInstancePort)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            "terminal-durability",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rootKey = Enumerable.Repeat((byte)0x4d, 32).ToArray();
        var options = new DurableStorageOptions
        {
            RootDirectory = root,
            EncryptionKey = DurableStorageKeyDerivation.Derive(rootKey, "records"),
            SegmentMaxBytes = 64 * 1024,
            SegmentMaxAge = TimeSpan.FromMinutes(1),
            AcquireWriterLease = false,
        };
        var records = new DurableSegmentStore(options);

        try
        {
            var paths = new DurableStreamPathEncoder(root);
            var artifacts = new ExecutionArtifactStore(
                root,
                DurableStorageKeyDerivation.Derive(rootKey, "artifacts"));
            var diagnostics = new ExecutionDiagnosticStore(
                records,
                new DurableCursorCodec(
                    DurableStorageKeyDerivation.Derive(rootKey, "cursors"),
                    paths),
                artifacts);
            var probe = new SealBeforeSaveProbe(records);
            var dbOptions = new DbContextOptionsBuilder<SharpClawDbContext>()
                .UseInMemoryDatabase("TerminalDurability_" + Guid.NewGuid().ToString("N"))
                .AddInterceptors(probe)
                .Options;

            await using var db = new SharpClawDbContext(dbOptions);
            var instance = new TaskInstanceDB
            {
                Id = Guid.NewGuid(),
                TaskDefinitionId = Guid.NewGuid(),
                Status = TaskInstanceStatus.Running,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            };
            db.TaskInstances.Add(instance);
            await db.SaveChangesAsync();

            await diagnostics.AppendTaskLogAsync(
                instance.Id,
                "terminal event",
                "Information");
            await diagnostics.AppendTaskOutputAsync(instance.Id, "{\"done\":true}");
            records.GetSnapshot().ActiveStreams.Should().Be(2);

            var persistence = new DurableExecutionPersistence(db, diagnostics, artifacts);
            var host = new EfTaskAdministrationHost(
                db,
                new EfPersistenceEntityResolver(),
                null!,
                persistence);
            probe.Arm();

            if (persistThroughInstancePort)
            {
                await host.PersistInstanceAsync(
                    new TaskInstanceState
                    {
                        Id = instance.Id,
                        TaskDefinitionId = instance.TaskDefinitionId,
                        Status = terminalStatus,
                        CreatedAt = instance.CreatedAt,
                        StartedAt = instance.StartedAt,
                        CompletedAt = DateTimeOffset.UtcNow,
                    },
                    CancellationToken.None);
            }
            else
            {
                instance.Status = terminalStatus;
                instance.CompletedAt = DateTimeOffset.UtcNow;
                await host.SaveAsync(CancellationToken.None);
            }

            probe.ActiveStreamsObservedAtSave.Should().Equal(0);
            records.GetSnapshot().ActiveStreams.Should().Be(0);
        }
        finally
        {
            await records.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SealBeforeSaveProbe(DurableSegmentStore records)
        : SaveChangesInterceptor
    {
        private bool _armed;

        public List<int> ActiveStreamsObservedAtSave { get; } = [];

        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed)
                ActiveStreamsObservedAtSave.Add(records.GetSnapshot().ActiveStreams);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
