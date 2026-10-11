namespace SharpClaw.Services;

internal sealed partial class ClientStartupLifetime
{
    private readonly List<Task> _acquisitions = [];

    internal Task<T?> AcquireOwnedAsync<T>(Func<Task<T>> acquire, Action<T> publish,
        Func<T, ValueTask> retire) where T : class
    {
        // Reserve the actual task's ownership BEFORE invoking a factory that can
        // suspend, synchronously publish a window, or reenter shutdown.
        var reservation = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _acquisitions.Add(reservation.Task.Unwrap());
        }
        // Start on the caller's UI thread. Only adoption and retirement after
        // acquisition use context-free continuations; no Task.Run for navigation.
        var actual = AcquireAndPublishAsync(acquire, publish, retire);
        reservation.SetResult(actual);
        return actual;
    }

    private async Task<T?> AcquireAndPublishAsync<T>(Func<Task<T>> acquire, Action<T> publish,
        Func<T, ValueTask> retire) where T : class
    {
        var resource = await acquire().ConfigureAwait(false);
        if (TryPublishOwned(resource, publish)) return resource;
        // A closed window cannot adopt the Host. Its actual asynchronous disposal
        // is part of this retained operation, not a later UI continuation.
        await retire(resource).ConfigureAwait(false);
        return null;
    }
}
