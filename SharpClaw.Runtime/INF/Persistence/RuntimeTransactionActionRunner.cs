using System.Data;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.INF.Persistence;


public sealed class RuntimeTransactionActionRunner(
    SharpClawDbContext db,
    IRuntimeTransactionActionBoundary actionBoundary) : IRuntimeTransactionActionRunner
{
    private readonly Func<CancellationToken, Task<IDbContextTransaction>> _beginTransaction = token =>
        db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

    internal RuntimeTransactionActionRunner(SharpClawDbContext db, IRuntimeTransactionActionBoundary actionBoundary,
        Func<CancellationToken, Task<IDbContextTransaction>> beginTransaction) : this(db, actionBoundary)
    { _beginTransaction = beginTransaction; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Join actual transaction acquisition and dispose its untransferred handle after any Core receipt failure, preserving the initiating exception and observing secondary cleanup failures.")]
    public async Task<IDbContextTransaction?> BeginSerializableAsync(
        CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is not null)
            return null;

        var admission = new Lock();
        var closed = false;
        Task<IDbContextTransaction>? physical = null;
        IDbContextTransaction? acquired = null;
        ExceptionDispatchInfo? failure = null;
        RuntimeTransactionActionResult result = default;
        try
        {
            result = await actionBoundary.RunTransactionActionAsync(
                new RuntimeTransactionActionInvocation(new SharpClawActionKey("storage.transaction.begin"),
                    IsolationLevel.Serializable, HasExistingTransaction: false),
                async actionToken =>
                {
                    Task<IDbContextTransaction> work;
                    lock (admission)
                    {
                        if (closed) throw new InvalidOperationException("Transaction begin admission has closed.");
                        physical ??= Task.Run(() => _beginTransaction(actionToken), CancellationToken.None);
                        work = physical;
                    }
                    await work.ConfigureAwait(false);
                    // Only bounded receipt data crosses the Core snapshot boundary.
                    return new RuntimeTransactionActionResult(null) { BeganTransaction = true };
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        Task<IDbContextTransaction>? owned;
        lock (admission) { closed = true; owned = physical; }
        if (owned is not null)
        {
            try { acquired = await owned.ConfigureAwait(false); }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        if (failure is null && result.BeganTransaction && acquired is not null) return acquired;
        failure ??= ExceptionDispatchInfo.Capture(new InvalidOperationException("The transaction receipt did not transfer an admitted acquisition."));
        if (acquired is not null)
        {
            try { await acquired.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { ObserveCompensationFailure(exception); }
        }
        failure.Throw();
        throw new InvalidOperationException("Unreachable transaction transfer.");
    }

    private static void ObserveCompensationFailure(Exception failure)
    {
        try { System.Diagnostics.Trace.TraceWarning("Transaction compensation failed ({0}).", failure.GetType().FullName); }
        catch (Exception sinkFailure) when (sinkFailure is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Join the actual commit/rollback before its caller may retire the borrowed transaction. Retain the original Core receipt failure over late physical errors.")]
    private async Task RunTransactionOperationAsync(
        SharpClawActionKey actionKey,
        IDbContextTransaction transaction,
        Func<IDbContextTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(operation);

        var admission = new Lock();
        var closed = false;
        Task? physical = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            await actionBoundary.RunTransactionActionAsync(
                new RuntimeTransactionActionInvocation(actionKey, IsolationLevel: null, HasExistingTransaction: true),
                async actionToken =>
                {
                    Task work;
                    lock (admission)
                    {
                        if (closed) throw new InvalidOperationException("Transaction operation admission has closed.");
                        physical ??= Task.Run(() => operation(transaction, actionToken), CancellationToken.None);
                        work = physical;
                    }
                    await work.ConfigureAwait(false);
                    return RuntimeTransactionActionResult.Completed;
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        Task? owned;
        lock (admission) { closed = true; owned = physical; }
        if (owned is not null)
        {
            try { await owned.ConfigureAwait(false); }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        failure?.Throw();
        if (owned is null) throw new InvalidOperationException("The transaction operation terminal was not admitted.");
    }
}
