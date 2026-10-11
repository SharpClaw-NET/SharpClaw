namespace SharpClaw.Tests.Kernel;

/// <summary>One bootstrap timer is explicitly expired after the probe enters.</summary>
internal sealed class InFlightBudgetClock : TimeProvider
{
    private Action? _expire;
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (_expire is not null || period != Timeout.InfiniteTimeSpan)
            throw new InvalidOperationException("This fixture owns one one-shot bootstrap timer.");
        var timer = new BudgetTimer(callback, state);
        _expire = () =>
        {
            Interlocked.Exchange(ref _timestamp, dueTime.Ticks);
            timer.Fire();
        };
        // CancellationTokenSource owns and disposes the returned timer; the
        // clock's delegate only permits a controlled delivery to that owner.
        return timer;
    }

    internal void ExpireBudget() => (_expire ?? throw new InvalidOperationException("No bootstrap timer was admitted."))();

    private sealed class BudgetTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;
        internal void Fire()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
