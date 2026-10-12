namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Owns admitted participant startup independently of the Core receipt.</summary>
internal sealed class RuntimeStartupWork(Func<CancellationToken, ValueTask> operation)
{
    private readonly Lock _gate = new();
    private Task? _work;
    private bool _closed;

    internal bool WasInvoked
    {
        get { lock (_gate) return _work is not null; }
    }

    internal ValueTask RunAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closed)
                throw new InvalidOperationException("Runtime startup admission has closed.");
            return new ValueTask(_work ??= Task.Run(async () =>
                await operation(cancellationToken).ConfigureAwait(false), CancellationToken.None));
        }
    }

    internal ValueTask JoinAsync()
    {
        lock (_gate)
        {
            _closed = true;
            return _work is { } work ? new ValueTask(work) : ValueTask.CompletedTask;
        }
    }
}
