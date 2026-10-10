using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.INF.Persistence;


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
#pragma warning disable VSTHRD003 // A context-free transaction terminal joins its own once-only completion; no UI or JoinableTask context is involved.
                return await completion.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003

            try
            {
                var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    actionCancellationToken).ConfigureAwait(false);
                var terminalResult = new RuntimeTransactionActionResult(transaction);
                completion.TrySetResult(terminalResult);
                return terminalResult;
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
            cancellationToken).ConfigureAwait(false);
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
#pragma warning disable VSTHRD003 // Repeated context-free terminal invocations must join the same transaction operation, not start it twice.
                return await completion.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003

            try
            {
                await operation(transaction, actionCancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
    }
}
