using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Trigger source that fires on SharpClaw host events
/// (<see cref="AgentOrchestrationTriggerKeys.Event"/>,
/// <see cref="AgentOrchestrationTriggerKeys.TaskCompleted"/>,
/// <see cref="AgentOrchestrationTriggerKeys.TaskFailed"/>).
/// Implements <see cref="ISharpClawEventSink"/> so the host dispatcher
/// delivers events directly to it.
/// <para>
/// Moved out of <c>SharpClaw.Runtime.BLL</c> by the trigger-extraction
/// plan; behavior is preserved verbatim.
/// </para>
/// </summary>
public sealed class EventBusTriggerSource(
    ISharpClawEventSinkRegistry sinkRegistry,
    ILogger<EventBusTriggerSource> logger) : ITaskTriggerSource, ISharpClawEventSink
{
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    // ── ITaskTriggerSource ────────────────────────────────────────

    public IReadOnlyList<string> TriggerKeys { get; } =
    [
        AgentOrchestrationTriggerKeys.Event,
    ];

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        _contexts = contexts;
        sinkRegistry.InvalidateCache();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _contexts = [];
        sinkRegistry.InvalidateCache();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public string? GetBindingValue(TaskTriggerDefinition def) => def.TriggerKey switch
    {
        AgentOrchestrationTriggerKeys.Event =>
            def.Parameters.GetValueOrDefault(AgentOrchestrationTriggerKeys.EventType),
        _ => null,
    };

    /// <inheritdoc />
    public string? GetBindingFilter(TaskTriggerDefinition def) =>
        def.TriggerKey == AgentOrchestrationTriggerKeys.Event
            ? def.Parameters.GetValueOrDefault(AgentOrchestrationTriggerKeys.EventFilter)
            : null;

    // ── ISharpClawEventSink ───────────────────────────────────────

    public SharpClawEventType SubscribedEvents =>
        SharpClawEventType.All;

    public async Task OnEventAsync(SharpClawEvent evt, CancellationToken ct)
    {
        foreach (var ctx in _contexts)
        {
            if (!MatchesContext(ctx, evt)) continue;

            try
            {
                await ctx.FireAsync(ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "EventBusTriggerSource failed to fire context for definition {Id}.",
                    ctx.TaskDefinitionId);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static bool MatchesContext(ITaskTriggerSourceContext ctx, SharpClawEvent evt)
    {
        var def = ctx.Definition;
        if (def.TriggerKey != AgentOrchestrationTriggerKeys.Event) return false;

        var eventType =
            def.Parameters.GetValueOrDefault(AgentOrchestrationTriggerKeys.EventType);
        if (string.IsNullOrWhiteSpace(eventType)) return false;
        if (!MatchesEventTypeFilter(eventType, evt.Type)) return false;
        var filter =
            def.Parameters.GetValueOrDefault(AgentOrchestrationTriggerKeys.EventFilter);
        if (!string.IsNullOrWhiteSpace(filter) &&
            !MatchesEventFilter(filter, evt))
            return false;

        return true;
    }

    private static bool MatchesEventTypeFilter(string eventTypeFilter, SharpClawEventType actual)
    {
        foreach (var part in eventTypeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<SharpClawEventType>(part, ignoreCase: true, out var parsed) &&
                actual.HasFlag(parsed))
                return true;
        }

        return false;
    }

    private static bool MatchesEventFilter(string filter, SharpClawEvent evt) =>
        (evt.SourceId is not null && evt.SourceId.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
        (evt.Summary  is not null && evt.Summary.Contains(filter,  StringComparison.OrdinalIgnoreCase));
}
