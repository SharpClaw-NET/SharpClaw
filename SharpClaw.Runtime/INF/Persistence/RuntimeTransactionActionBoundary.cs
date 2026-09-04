using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.INF.Persistence;

public readonly record struct RuntimeTransactionActionResult(
    IDbContextTransaction? Transaction)
{
    public static RuntimeTransactionActionResult Completed => new(null);
}

public sealed record RuntimeTransactionActionInvocation(
    SharpClawActionKey ActionKey,
    IsolationLevel? IsolationLevel,
    bool HasExistingTransaction);

public interface IRuntimeTransactionActionBoundary
{
    ValueTask<RuntimeTransactionActionResult> RunTransactionActionAsync(
        RuntimeTransactionActionInvocation invocation,
        Func<CancellationToken, ValueTask<RuntimeTransactionActionResult>> terminal,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeTransactionActionRunner
{
    Task<IDbContextTransaction?> BeginSerializableAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default);

    Task RollbackAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeTransactionActionRunnerAccessor
{
    RuntimeTransactionActionRunner GetRequiredRunner();
}

public sealed class RuntimeTransactionActionRunnerAccessor(IServiceProvider services)
    : IRuntimeTransactionActionRunnerAccessor
{
    public RuntimeTransactionActionRunner GetRequiredRunner() =>
        services.GetRequiredService<RuntimeTransactionActionRunner>();
}

public sealed class RuntimeTransactionActionRunner(
    SharpClawDbContext db,
    IRuntimeTransactionActionBoundary actionBoundary) : IRuntimeTransactionActionRunner
{
    public async Task<IDbContextTransaction?> BeginSerializableAsync(
        CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is not null)
            return null;

        var completion = new TaskCompletionSource<RuntimeTransactionActionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalStarted = 0;
        async ValueTask<RuntimeTransactionActionResult> BeginTerminalAsync(
            CancellationToken actionCancellationToken)
        {
            if (Interlocked.CompareExchange(ref terminalStarted, 1, 0) != 0)
                return await completion.Task;

            try
            {
                var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    actionCancellationToken);
                var result = new RuntimeTransactionActionResult(transaction);
                completion.TrySetResult(result);
                return result;
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                throw;
            }
        }

        var result = await actionBoundary.RunTransactionActionAsync(
            new RuntimeTransactionActionInvocation(
                new SharpClawActionKey("storage.transaction.begin"),
                IsolationLevel.Serializable,
                HasExistingTransaction: false),
            BeginTerminalAsync,
            cancellationToken);
        return result.Transaction;
    }

    public Task CommitAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default) =>
        RunTransactionOperationAsync(
            new SharpClawActionKey("storage.transaction.commit"),
            transaction,
            static (current, ct) => current.CommitAsync(ct),
            cancellationToken);

    public Task RollbackAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default) =>
        RunTransactionOperationAsync(
            new SharpClawActionKey("storage.transaction.rollback"),
            transaction,
            static (current, ct) => current.RollbackAsync(ct),
            cancellationToken);

    private async Task RunTransactionOperationAsync(
        SharpClawActionKey actionKey,
        IDbContextTransaction transaction,
        Func<IDbContextTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(operation);

        var completion = new TaskCompletionSource<RuntimeTransactionActionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalStarted = 0;
        async ValueTask<RuntimeTransactionActionResult> OperationTerminalAsync(
            CancellationToken actionCancellationToken)
        {
            if (Interlocked.CompareExchange(ref terminalStarted, 1, 0) != 0)
                return await completion.Task;

            try
            {
                await operation(transaction, actionCancellationToken);
                var result = RuntimeTransactionActionResult.Completed;
                completion.TrySetResult(result);
                return result;
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                throw;
            }
        }

        await actionBoundary.RunTransactionActionAsync(
            new RuntimeTransactionActionInvocation(
                actionKey,
                IsolationLevel: null,
                HasExistingTransaction: true),
            OperationTerminalAsync,
            cancellationToken);
    }
}
