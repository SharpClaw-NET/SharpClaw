using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Runtime.INF.Persistence;

public sealed record RuntimePersistenceActionInvocation(
    SharpClawActionKey ActionKey,
    int AddedCount,
    int ModifiedCount,
    int DeletedCount);

public interface IRuntimePersistenceActionBoundary
{
    ValueTask RunPersistenceActionAsync(
        RuntimePersistenceActionInvocation invocation,
        Func<CancellationToken, ValueTask<int>> terminal,
        CancellationToken cancellationToken = default);
}

public interface IRuntimePersistenceActionRunnerAccessor
{
    RuntimePersistenceActionRunner GetRequiredRunner();
}

public sealed class RuntimePersistenceActionRunnerAccessor(IServiceProvider services)
    : IRuntimePersistenceActionRunnerAccessor
{
    public RuntimePersistenceActionRunner GetRequiredRunner() =>
        services.GetRequiredService<RuntimePersistenceActionRunner>();
}

public sealed class RuntimePersistenceActionRunner(
    IRuntimePersistenceActionBoundary actionBoundary)
{
    public async ValueTask<int> SaveChangesAsync(
        SharpClawDbContext db,
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
                return await terminal.Task.WaitAsync(actionCancellationToken);

            try
            {
                var saved = await db.SaveChangesTerminalAsync(actionCancellationToken);
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
            cancellationToken);

        if (Volatile.Read(ref terminalStarted) == 0)
        {
            throw new InvalidOperationException(
                "Persistence action completed without running its save terminal.");
        }

        return await terminal.Task;
    }
}
