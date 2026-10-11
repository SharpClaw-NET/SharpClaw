namespace SharpClaw.Tests.Frontend;

internal sealed class ClosedStartupContext : SynchronizationContext
{
    private int _posts;
    internal int Posts => Volatile.Read(ref _posts);
    internal Task<T> InvokeAsync<T>(Func<Task<T>> start)
    {
        var previous = Current;
        try
        {
            SetSynchronizationContext(this);
            return start();
        }
        finally { SetSynchronizationContext(previous); }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        Interlocked.Increment(ref _posts);
        throw new InvalidOperationException("The closed window cannot dispatch startup retirement.");
    }
}
