namespace SharpClaw.Gateway.Infrastructure;

/// <summary>Retains one actual terminal independently of its bounded Core receipt.</summary>
internal sealed class GatewayActionTerminal<TPayload, TResult>(Func<TPayload, CancellationToken, ValueTask<TResult>> terminal)
{
    private readonly Lock _admission = new();
    private Task<TResult>? _work;
    private bool _closed;

    internal bool WasInvoked { get { lock (_admission) return _work is not null; } }

    internal ValueTask<TResult> RunAsync(TPayload payload, CancellationToken cancellationToken)
    {
        lock (_admission)
        {
            if (_closed) throw new InvalidOperationException("Gateway terminal admission has closed.");
            _work ??= Task.Run(async () => await terminal(payload, cancellationToken).ConfigureAwait(false), CancellationToken.None);
            return new ValueTask<TResult>(_work);
        }
    }

    internal ValueTask JoinAsync()
    {
        lock (_admission)
        {
            _closed = true;
            return _work is { } work ? new ValueTask(work) : ValueTask.CompletedTask;
        }
    }

    internal ValueTask<TResult> ResultAsync()
    {
        lock (_admission)
            return new ValueTask<TResult>(_work ?? throw new InvalidOperationException("No physical Gateway terminal was admitted."));
    }
}
