using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class MigrationAdmissionTests
{
    [Test]
    public async Task CancelledDrainReopensAdmissionAndReleasesMigrationOwnershipAsync()
    {
        using var gate = new MigrationGate();
        using var cancellation = new CancellationTokenSource();
        using var existing = await gate.EnterRequestAsync(CancellationToken.None).ConfigureAwait(false);
        var migration = gate.EnterMigrationAsync(cancellation.Token);
        gate.State.Should().Be(MigrationState.Draining);
        using var queuedCancellation = new CancellationTokenSource();
        var queued = gate.EnterRequestAsync(queuedCancellation.Token).AsTask();
        try
        {
        queued.IsCompleted.Should().BeFalse();
        await cancellation.CancelAsync().ConfigureAwait(false);
        (await TestTaskOutcome.CaptureAsync(migration).ConfigureAwait(false)).Should().BeAssignableTo<OperationCanceledException>();
        gate.State.Should().Be(MigrationState.Idle);
        using var resumed = await queued.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        resumed.Dispose(); existing.Dispose(); existing.Dispose();
        using var next = await gate.EnterMigrationAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        gate.State.Should().Be(MigrationState.Migrating);
        next.Dispose(); next.Dispose();
        using var final = await gate.EnterRequestAsync(CancellationToken.None).ConfigureAwait(false);
        gate.State.Should().Be(MigrationState.Idle);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await queuedCancellation.CancelAsync().ConfigureAwait(false);
            existing.Dispose();
            _ = await TestTaskOutcome.CaptureAsync(migration).ConfigureAwait(false);
            try { using var settled = await queued.ConfigureAwait(false); }
            catch (OperationCanceledException) when (queuedCancellation.IsCancellationRequested) { }
        }
    }
}
