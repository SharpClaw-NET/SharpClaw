namespace SharpClaw.Infrastructure.Persistence;

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

    /// <summary>Current state of the migration gate.</summary>
    public MigrationState State { get; private set; } = MigrationState.Idle;

    /// <summary>
    /// Called by middleware. Returns immediately when no migration is
    /// running. Returns a disposable that decrements the in-flight counter.
    /// </summary>
    public async ValueTask<IDisposable> EnterRequestAsync(CancellationToken ct = default)
    {
        await _gate.Task.WaitAsync(ct);
        Interlocked.Increment(ref _inflightRequests);
        return new RequestHandle(this);
    }

    /// <summary>
    /// Called by MigrationService. Closes the gate, waits for all
    /// in-flight requests to complete, then returns a disposable
    /// that reopens the gate.
    /// </summary>
    public async Task<IDisposable> EnterMigrationAsync(CancellationToken ct = default)
    {
        await _migrationLock.WaitAsync(ct);

        // Close gate — new requests will await.
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        State = MigrationState.Draining;

        // Wait for in-flight requests to drain.
        // Set the drain TCS BEFORE reading the counter to avoid a race where
        // DecrementInflight sees _currentDrain == null between the read and assignment.
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _currentDrain = drain;
        if (Volatile.Read(ref _inflightRequests) == 0)
            drain.TrySetResult();
        await drain.Task.WaitAsync(ct);

        State = MigrationState.Migrating;
        return new MigrationHandle(this);
    }

    private void DecrementInflight()
    {
        if (Interlocked.Decrement(ref _inflightRequests) == 0)
            _currentDrain?.TrySetResult();
    }

    private void ReleaseMigration()
    {
        State = MigrationState.Idle;
        var g = _gate;
        _gate = CreateOpenGate();
        g.TrySetResult(); // release any requests that queued during close
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

    private sealed class RequestHandle(MigrationGate g) : IDisposable
    {
        public void Dispose() => g.DecrementInflight();
    }

    private sealed class MigrationHandle(MigrationGate g) : IDisposable
    {
        public void Dispose() => g.ReleaseMigration();
    }
}

/// <summary>Migration gate states.</summary>
public enum MigrationState { Idle, Draining, Migrating }
