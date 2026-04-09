namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Observe-only event sink for host lifecycle events. Modules implement this
/// interface and register it via <see cref="ISharpClawModule.ConfigureServices"/>.
/// <para>
/// Sinks are fire-and-forget: the host dispatches events after the operation
/// completes and does not wait for sinks. Exceptions from sinks are logged
/// and swallowed — a broken sink cannot affect the host.
/// </para>
/// <para>
/// Sinks must be fast (&lt; 100ms). Long-running reactions should enqueue work
/// to a background channel and return immediately.
/// </para>
/// </summary>
public interface ISharpClawEventSink
{
    /// <summary>Declares which events this sink wants to receive.</summary>
    SharpClawEventType SubscribedEvents { get; }

    /// <summary>
    /// Handle an event. Called by the host dispatcher after the operation
    /// completes. Must not throw — exceptions are logged and swallowed.
    /// </summary>
    Task OnEventAsync(SharpClawEvent evt, CancellationToken ct);
}
