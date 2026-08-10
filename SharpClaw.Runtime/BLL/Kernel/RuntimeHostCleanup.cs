using System.Runtime.ExceptionServices;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Runs all host shutdown operations once and preserves the first failure.</summary>
internal sealed class RuntimeHostCleanup(
    Action markNotReady,
    Action deleteDiscoveryEntry,
    Action cleanupApiKey,
    Func<ValueTask> stopListener)
{
    private int _attempted;

    public bool Attempted => Volatile.Read(ref _attempted) == 1;

    public async ValueTask RunAsync()
    {
        if (Interlocked.Exchange(ref _attempted, 1) != 0)
            return;

        ExceptionDispatchInfo? failure = null;
        Try(markNotReady, ref failure);
        Try(deleteDiscoveryEntry, ref failure);
        Try(cleanupApiKey, ref failure);

        try
        {
            await stopListener();
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }

        failure?.Throw();
    }

    private static void Try(Action operation, ref ExceptionDispatchInfo? failure)
    {
        try
        {
            operation();
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }
    }
}
