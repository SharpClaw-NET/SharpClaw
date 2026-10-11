using System.Runtime.ExceptionServices;

namespace SharpClaw.Services;

/// <summary>Native host completion cannot retire the process before application cleanup.</summary>
internal sealed class ClientDesktopLifetime
{
    private readonly TaskCompletionSource _nativeStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    internal Task RunHostAsync(Func<Task> runHost, Func<Task> shutdown)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The desktop lifetime already owns a native host.");
        return RunAsync(() => ObserveHostAsync(runHost), shutdown);
    }

    internal async ValueTask RetireAfterHostAsync(Func<ValueTask> retire)
    {
        // Window.Closed starts cleanup while the native loop can still log through
        // the DI Host. Only actual native-loop settlement releases its dependencies.
#pragma warning disable VSTHRD003 // This private signal is completed only after the one actual RunHostAsync native task settles. Cleanup runs context-free; it neither dispatches to the closed window nor holds the native loop waiting for shutdown.
        await _nativeStopped.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        await retire().ConfigureAwait(false);
    }

    private async Task ObserveHostAsync(Func<Task> runHost)
    {
        try { await runHost().ConfigureAwait(false); }
        finally { _nativeStopped.TrySetResult(); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "The desktop exit owner retains the original host failure while still joining application shutdown, then rethrows that first failure. A later shutdown failure becomes primary only when host execution succeeded.")]
    internal static async Task RunAsync(Func<Task> runHost, Func<Task> shutdown)
    {
        ExceptionDispatchInfo? failure = null;
        try { await runHost().ConfigureAwait(false); }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        try { await shutdown().ConfigureAwait(false); }
        catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        failure?.Throw();
    }
}
