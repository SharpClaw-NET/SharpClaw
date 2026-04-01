namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Singleton signal that bot services can await. When <see cref="Signal"/>
/// is called (e.g. after a bot config mutation), all waiters are released
/// so they can re-fetch configuration and restart polling.
/// </summary>
public sealed class BotReloadSignal
{
    private volatile TaskCompletionSource _tcs = new();

    /// <summary>
    /// Signals all waiters to wake up and re-fetch config.
    /// </summary>
    public void Signal()
        => Interlocked.Exchange(ref _tcs, new TaskCompletionSource()).TrySetResult();

    /// <summary>
    /// Waits until a reload signal is fired or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        var tcs = _tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await tcs.Task;
    }
}
