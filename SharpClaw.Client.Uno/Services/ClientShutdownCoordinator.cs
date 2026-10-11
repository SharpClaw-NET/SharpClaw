using System.Runtime.ExceptionServices;

namespace SharpClaw.Services;

/// <summary>Owns required resource teardown independently of the close-action receipt.</summary>
internal sealed class ClientShutdownCoordinator
{
    private readonly Lock _gate = new();
    private Task? _shutdown;

    internal Task ShutdownAsync(BundledBackendMutationCoordinator? mutations,
        ClientActionDispatcher? actions, Func<Task> cleanup)
    {
        lock (_gate)
        {
            if (_shutdown is not null) return _shutdown;
            // Close shared admission before the first await or close-action hook.
            var drain = mutations?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            // This cached mutation shutdown owns a context-free Task.Run drain;
            // retain it across the shutdown task and join it before resource cleanup.
#pragma warning disable VSTHRD003
            _shutdown = Task.Run(() => RunAsync(actions, drain, cleanup), CancellationToken.None);
#pragma warning restore VSTHRD003
            return _shutdown;
        }
    }

    private static async Task RunAsync(ClientActionDispatcher? actions, Task drain, Func<Task> cleanup)
    {
        var gate = new Lock();
        Task? cleanupWork = null;
        Task StartCleanupAsync()
        {
            lock (gate)
            {
                // The captured drain is the same owned mutation shutdown above.
                // The retained cleanup task joins it before disposing any owner.
#pragma warning disable VSTHRD003
                return cleanupWork ??= Task.Run(() => DrainThenCleanupAsync(drain, cleanup), CancellationToken.None);
#pragma warning restore VSTHRD003
            }
        }

        try
        {
            if (actions is not null)
                await actions.RunCommandAsync("client.app.close", _ => new ValueTask(StartCleanupAsync()),
                    CancellationToken.None).ConfigureAwait(false);
            // An early successful receipt still requires successful physical cleanup.
            await JoinCleanupAsync(StartCleanupAsync()).ConfigureAwait(false);
        }
        finally
        {
            // Rejected/skipped/late close terminals cannot abandon required teardown.
            // Its retained task starts once and joins the shared mutation drain first.
            await ObserveCleanupAsync(StartCleanupAsync()).ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Required cleanup runs even if the shared mutation drain faults; secondary cleanup failures are journalled and the first failure keeps its original stack.")]
    private static async Task DrainThenCleanupAsync(Task drain, Func<Task> cleanup)
    {
        ExceptionDispatchInfo? failure = null;
        try
        {
            // This cached context-free mutation shutdown was created before admission
            // closed. It must settle before any process or HTTP owner is disposed.
#pragma warning disable VSTHRD003
            await drain.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        try { await cleanup().ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (failure is null) failure = ExceptionDispatchInfo.Capture(exception);
            else ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
        failure?.Throw();
    }

    private static async Task JoinCleanupAsync(Task cleanup)
    {
        // The supplied task is retained Task.Run cleanup owned by this shutdown.
        // No UI continuation is required by the process/resource teardown.
#pragma warning disable VSTHRD003
        await cleanup.ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Physical cleanup is joined even after a close receipt fault; secondary cleanup failure is observed without replacing the original receipt fault or cancellation.")]
    private static async Task ObserveCleanupAsync(Task cleanup)
    {
        try
        {
            // The caller supplies the retained context-free cleanup Task.Run.
            // Observe its physical join while retaining a prior close receipt fault.
#pragma warning disable VSTHRD003
            await JoinCleanupAsync(cleanup).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
    }
}
