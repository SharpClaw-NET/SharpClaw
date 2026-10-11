namespace SharpClaw.Services;

/// <summary>One frontend instance owns admission and physical settlement of every bundled-backend change.</summary>
internal sealed class BundledBackendMutationCoordinator : IAsyncDisposable
{
    private readonly Lock _gate = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "The cached context-free DisposeAsync task cancels this source, joins the retained mutation and physical terminal, then disposes this source in finally. The analyzer cannot trace that Task.Run/ValueTask ownership path; disposal must follow the active-work join.")]
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<string, BundledBackendMutationStage, CancellationToken, Task>? _observer;
    private Task? _active;
    private Task? _shutdown;

    internal BundledBackendMutationCoordinator(
        Func<string, BundledBackendMutationStage, CancellationToken, Task>? observer = null)
    {
        _observer = observer;
    }

    internal Task RunAsync(ClientActionDispatcher actions, string operation,
        Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        RunAsync(actions, operation, async token =>
        {
            await work(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    internal Task<TResult> RunAsync<TResult>(ClientActionDispatcher actions, string operation,
        Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            if (_active is { IsCompleted: false })
                throw new InvalidOperationException("A bundled backend change is already in progress. Retry after it finishes.");
            var active = Task.Run(() => RunAdmittedAsync(actions, operation, work, cancellationToken), CancellationToken.None);
            _active = active;
            return active;
        }
    }

    internal Task CheckpointAsync(string operation, BundledBackendMutationStage stage, CancellationToken cancellationToken) =>
        _observer?.Invoke(operation, stage, cancellationToken) ?? Task.CompletedTask;

    private async Task<TResult> RunAdmittedAsync<TResult>(ClientActionDispatcher actions, string operation,
        Func<CancellationToken, Task<TResult>> change, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var admission = new Lock();
        var accepting = true;
        Task<TResult>? work = null;
        try
        {
            var result = await actions.RunCommandAsync(operation, token =>
            {
                lock (admission)
                {
                    if (!accepting || work is not null)
                        throw new InvalidOperationException("This bundled backend change may run only once.");
                    work = Task.Run(() => change(token), CancellationToken.None);
                    return new ValueTask<TResult>(work);
                }
            }, lifetime.Token).ConfigureAwait(false);
            Task<TResult> completedWork;
            lock (admission)
            {
                accepting = false;
                if (work is null) throw new InvalidOperationException("The bundled backend change was not accepted.");
                completedWork = work;
            }
            // A successful/transformed receipt still promises successful physical
            // settlement. A prior receipt fault remains primary in the finally join.
            _ = await completedWork.ConfigureAwait(false);
            return result;
        }
        finally
        {
            Task<TResult>? ownedWork;
            lock (admission)
            {
                accepting = false;
                ownedWork = work;
            }
            if (ownedWork is not null) await ObserveSettlementAsync(ownedWork).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            // Publish closure synchronously, before cancellation or joining can yield.
            var shutdown = _shutdown ??= Task.Run(DisposeCoreAsync, CancellationToken.None);
            GC.SuppressFinalize(this);
            return new ValueTask(shutdown);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Shutdown observes cancellation callback faults using bounded metadata and still joins actual active work before disposing the lifetime source.")]
    private async Task DisposeCoreAsync()
    {
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
        finally
        {
            Task? active;
            lock (_gate) active = _active;
            try { if (active is not null) await ObserveSettlementAsync(active).ConfigureAwait(false); }
            finally { _lifetime.Dispose(); }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "The retained physical task is joined after receipt settlement. Secondary failure metadata is observed without replacing the original receipt fault/cancellation.")]
    private static async Task ObserveSettlementAsync(Task work)
    {
        try
        {
            // Both callers supply a retained Task.Run owned by this coordinator.
            // No UI continuation is needed; disposal must wait for physical work.
#pragma warning disable VSTHRD003
            await work.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
    }
}
