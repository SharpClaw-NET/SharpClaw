using System.Runtime.ExceptionServices;

namespace SharpClaw.Services;

/// <summary>Native host completion cannot retire the process before application cleanup.</summary>
internal static class ClientDesktopLifetime
{
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
