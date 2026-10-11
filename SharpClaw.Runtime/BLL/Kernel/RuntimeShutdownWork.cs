namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Retains one context-free physical shutdown phase independently of its action receipt.</summary>
internal sealed class RuntimeShutdownWork(Func<CancellationToken, ValueTask> operation)
{
    private readonly Lock _gate = new();
    private Task? _work;

    internal ValueTask RunAsync(CancellationToken _)
    {
        lock (_gate)
            return new ValueTask(_work ??= Task.Run(async () =>
                await operation(CancellationToken.None).ConfigureAwait(false), CancellationToken.None));
    }
}
