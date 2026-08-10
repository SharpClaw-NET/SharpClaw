using System.Runtime.ExceptionServices;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Runs host shutdown operations in the required lifecycle order.</summary>
internal sealed class RuntimeHostCleanup(
    Action markNotReady,
    Action deleteDiscoveryEntry,
    Action cleanupApiKey,
    Func<ValueTask> stopListener)
{
    private int _preparationAttempted;
    private int _completionAttempted;

    public bool PreparationAttempted => Volatile.Read(ref _preparationAttempted) == 1;

    public bool CompletionAttempted => Volatile.Read(ref _completionAttempted) == 1;

    public async ValueTask BeginAsync()
    {
        if (Interlocked.Exchange(ref _preparationAttempted, 1) != 0)
            return;

        ExceptionDispatchInfo? failure = null;
        Try(markNotReady, ref failure);

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

    public ValueTask CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completionAttempted, 1) != 0)
            return ValueTask.CompletedTask;

        ExceptionDispatchInfo? failure = null;
        Try(deleteDiscoveryEntry, ref failure);
        Try(cleanupApiKey, ref failure);
        failure?.Throw();
        return ValueTask.CompletedTask;
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
