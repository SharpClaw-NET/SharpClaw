using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Singleton dispatcher that routes host events to module event sinks.
/// Dispatch is fire-and-forget with per-sink error isolation.
/// </summary>
public sealed class ModuleEventDispatcher(
    IServiceProvider rootServices,
    ILogger<ModuleEventDispatcher> logger)
{
    /// <summary>Cached sink list — rebuilt when modules are enabled/disabled.</summary>
    private IReadOnlyList<ISharpClawEventSink>? _sinks;
    private readonly object _sinkLock = new();

    /// <summary>
    /// Dispatch an event to all sinks that subscribe to its type.
    /// Fire-and-forget — does not block the caller.
    /// </summary>
    public void Dispatch(SharpClawEvent evt)
    {
        var sinks = GetSinks();
        if (sinks.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var sink in sinks)
            {
                if (!sink.SubscribedEvents.HasFlag(evt.Type)) continue;

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await sink.OnEventAsync(evt, cts.Token);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Event sink {SinkType} threw while handling {EventType}.",
                        sink.GetType().Name, evt.Type);
                }
            }
        });
    }

    /// <summary>
    /// Invalidate the cached sink list. Called when modules are
    /// enabled or disabled at runtime.
    /// </summary>
    public void InvalidateSinkCache()
    {
        lock (_sinkLock) { _sinks = null; }
    }

    private IReadOnlyList<ISharpClawEventSink> GetSinks()
    {
        lock (_sinkLock)
        {
            _sinks ??= rootServices.GetServices<ISharpClawEventSink>().ToList();
            return _sinks;
        }
    }
}
