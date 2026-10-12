namespace SharpClaw.Runtime.INF.Persistence;

/// <summary>
/// Async-safe gate that pauses all request processing during migrations.
/// Normal requests: zero-cost await when gate is open.
/// Migration: closes the gate, waits for in-flight requests to drain,
/// then holds exclusive access until the migration completes.
/// </summary>
public sealed class MigrationGate : IDisposable
{
    // Open = requests flow through; closed = requests await until reopened.
    private volatile TaskCompletionSource _gate = CreateOpenGate();
    private int _inflightRequests;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private volatile TaskCompletionSource? _currentDrain;
    private readonly Lock _admission = new();

    /// <summary>Current state of the migration gate.</summary>
    public MigrationState State { get; private set; } = MigrationState.Idle;

    /// <summary>
    /// Called by middleware. Returns immediately when no migration is
    /// running. Returns a disposable that decrements the in-flight counter.
    /// </summary>
    public async ValueTask<IDisposable> EnterRequestAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task wait;
            lock (_admission)
            {
                ct.ThrowIfCancellationRequested();
                if (_gate.Task.IsCompleted)
                {
                    _inflightRequests++;
                    return new RequestHandle(DecrementInflight);
                }
                wait = _gate.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called by MigrationService. Closes the gate, waits for all
    /// in-flight requests to complete, then returns a disposable
    /// that reopens the gate.
    /// </summary>
    public async Task<IDisposable> EnterMigrationAsync(CancellationToken ct = default)
    {
        await _migrationLock.WaitAsync(ct).ConfigureAwait(false);

        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_admission)
        {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            State = MigrationState.Draining;
            _currentDrain = drain;
            if (_inflightRequests == 0) drain.TrySetResult();
        }
        try
        {
            await drain.Task.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (_admission) { State = MigrationState.Migrating; }
            return new MigrationHandle(ReleaseMigration);
        }
        catch
        {
            // No MigrationHandle received ownership of the closed admission/lock.
            ReleaseMigration();
            throw;
        }
    }

    private void DecrementInflight()
    {
        lock (_admission)
        {
            if (--_inflightRequests == 0) _currentDrain?.TrySetResult();
        }
    }

    private void ReleaseMigration()
    {
        lock (_admission)
        {
            State = MigrationState.Idle;
            _currentDrain = null;
            var g = _gate;
            _gate = CreateOpenGate();
            g.TrySetResult();
        }
        _migrationLock.Release();
    }

    private static TaskCompletionSource CreateOpenGate()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    /// <inheritdoc />
    public void Dispose() => _migrationLock.Dispose();

    private sealed class RequestHandle(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class MigrationHandle(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
