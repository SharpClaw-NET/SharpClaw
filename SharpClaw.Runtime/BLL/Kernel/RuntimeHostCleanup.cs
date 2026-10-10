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
    private readonly Lock _operationGate = new();
    private Task? _preparationTask;
    private Task? _completionTask;

    public bool PreparationAttempted => Volatile.Read(ref _preparationAttempted) == 1;

    public bool CompletionAttempted => Volatile.Read(ref _completionAttempted) == 1;

    public ValueTask BeginAsync()
    {
        lock (_operationGate)
        {
            _preparationTask ??= BeginCoreAsync();
            return new ValueTask(_preparationTask);
        }
    }

    private async Task BeginCoreAsync()
    {
        if (Interlocked.Exchange(ref _preparationAttempted, 1) != 0)
            return;

        ExceptionDispatchInfo? failure = null;
        Try(markNotReady, ref failure);

        try
        {
            await stopListener().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Capture the first cleanup failure, finish required operations, then rethrow through ExceptionDispatchInfo below.
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031

        failure?.Throw();
    }

    public ValueTask CompleteAsync()
    {
        lock (_operationGate)
        {
            if (_completionTask is null)
            {
                try
                {
                    CompleteCore();
                    _completionTask = Task.CompletedTask;
                }
#pragma warning disable CA1031 // Publish the cleanup failure to the retained completion task so every repeated caller observes the same settled outcome.
                catch (Exception exception)
                {
                    _completionTask = Task.FromException(exception);
                }
#pragma warning restore CA1031
            }
            return new ValueTask(_completionTask);
        }
    }

    private void CompleteCore()
    {
        if (Interlocked.Exchange(ref _completionAttempted, 1) != 0)
            return;
        ExceptionDispatchInfo? failure = null;
        Try(deleteDiscoveryEntry, ref failure);
        Try(cleanupApiKey, ref failure);
        failure?.Throw();
    }

    private static void Try(Action operation, ref ExceptionDispatchInfo? failure)
    {
        try
        {
            operation();
        }
#pragma warning disable CA1031 // Cleanup must attempt later owned resources even when an earlier operation fails; the caller rethrows the first failure.
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031
    }
}
