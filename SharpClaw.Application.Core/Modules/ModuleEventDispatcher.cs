using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Singleton dispatcher that routes host events to module event sinks.
/// Dispatch is fire-and-forget with per-sink error isolation. Sinks are
/// invoked in parallel under a shared per-event timeout, configured via
/// <c>Modules:EventDispatchTimeoutSeconds</c> (default 5 seconds).
/// </summary>
public sealed class ModuleEventDispatcher(
    IServiceProvider rootServices,
    IConfiguration configuration,
    ILogger<ModuleEventDispatcher> logger) : ISharpClawEventSinkRegistry
{
    /// <summary>Cached sink list — rebuilt when modules are enabled/disabled.</summary>
    private IReadOnlyList<ISharpClawEventSink>? _sinks;
    private readonly object _sinkLock = new();

    private TimeSpan DispatchTimeout =>
        TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Modules:EventDispatchTimeoutSeconds", 5)));

    /// <summary>
    /// Dispatch an event to all sinks that subscribe to its type.
    /// Fire-and-forget — does not block the caller. Unobserved exceptions
    /// from the background dispatch are logged.
    /// </summary>
    public void Dispatch(SharpClawEvent evt)
    {
        var sinks = GetSinks();
        if (sinks.Count == 0) return;

        var timeout = DispatchTimeout;
        var dispatch = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(timeout);
            var subscribed = sinks.Where(s => s.SubscribedEvents.HasFlag(evt.Type)).ToList();
            if (subscribed.Count == 0) return;

            var tasks = subscribed.Select(async sink =>
            {
                try
                {
                    await sink.OnEventAsync(evt, cts.Token);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Event sink {SinkType} threw while handling {EventType}.",
                        sink.GetType().Name, evt.Type);
                }
            });

            await Task.WhenAll(tasks);
        });

        _ = dispatch.ContinueWith(t =>
        {
            if (t.Exception is not null)
            {
                logger.LogError(t.Exception,
                    "Unobserved exception while dispatching {EventType}.", evt.Type);
            }
        }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// Invalidate the cached sink list. Called when modules are
    /// enabled or disabled at runtime.
    /// </summary>
    public void InvalidateSinkCache()
    {
        lock (_sinkLock) { _sinks = null; }
    }

    /// <inheritdoc />
    void ISharpClawEventSinkRegistry.InvalidateCache() => InvalidateSinkCache();

    private IReadOnlyList<ISharpClawEventSink> GetSinks()
    {
        lock (_sinkLock)
        {
            _sinks ??= ResolveSinks();
            return _sinks;
        }
    }

    private IReadOnlyList<ISharpClawEventSink> ResolveSinks()
    {
        var sinks = rootServices.GetServices<ISharpClawEventSink>().ToList();
        var registry = rootServices.GetService<ModuleRegistry>();
        if (registry is null)
            return sinks;

        foreach (var runtimeHost in registry.GetRuntimeHosts())
        {
            try
            {
                sinks.AddRange(runtimeHost.Services.GetServices<ISharpClawEventSink>());
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to resolve event sinks for module '{ModuleId}'.",
                    runtimeHost.Module.Id);
            }
        }

        return sinks;
    }
}
