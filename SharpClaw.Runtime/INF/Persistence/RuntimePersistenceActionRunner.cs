using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Persistence;

namespace SharpClaw.Runtime.INF.Persistence;


public sealed class RuntimePersistenceActionRunner(
    IRuntimePersistenceActionBoundary actionBoundary)
    : ISharpClawPersistenceSaveCoordinator
{
    Task<int> ISharpClawPersistenceSaveCoordinator.SaveChangesAsync(
        SharpClawDbContext db,
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken) =>
        SaveChangesAsync(db, acceptAllChangesOnSuccess, cancellationToken).AsTask();

    internal async ValueTask<int> SaveChangesAsync(
        SharpClawDbContext db,
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var entries = db.ChangeTracker.Entries().ToArray();
        var addedCount = entries.Count(entry => entry.State == EntityState.Added);
        var modifiedCount = entries.Count(entry => entry.State == EntityState.Modified);
        var deletedCount = entries.Count(entry => entry.State == EntityState.Deleted);
        var actionKey = deletedCount > 0 && addedCount == 0 && modifiedCount == 0
            ? new SharpClawActionKey("storage.delete.commit")
            : new SharpClawActionKey("storage.upsert.commit");
        var invocation = new RuntimePersistenceActionInvocation(
            actionKey,
            addedCount,
            modifiedCount,
            deletedCount);
        var terminal = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalStarted = 0;

        async ValueTask<int> SaveTerminalAsync(CancellationToken actionCancellationToken)
        {
            if (Interlocked.CompareExchange(ref terminalStarted, 1, 0) != 0)
                return await terminal.Task.WaitAsync(actionCancellationToken).ConfigureAwait(false);

            try
            {
                var saved = await db.SaveChangesTerminalAsync(
                    acceptAllChangesOnSuccess,
                    actionCancellationToken).ConfigureAwait(false);
                terminal.TrySetResult(saved);
                return saved;
            }
            catch (Exception exception)
            {
                terminal.TrySetException(exception);
                throw;
            }
        }

        await actionBoundary.RunPersistenceActionAsync(
            invocation,
            SaveTerminalAsync,
            cancellationToken).ConfigureAwait(false);

        if (Volatile.Read(ref terminalStarted) == 0)
        {
            throw new InvalidOperationException(
                "Persistence action completed without running its save terminal.");
        }

        return await terminal.Task.ConfigureAwait(false);
    }
}
