using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Trigger source that fires when another task definition completes
/// (<see cref="TaskScriptingTriggerKeys.TaskCompleted"/>) or fails
/// (<see cref="TaskScriptingTriggerKeys.TaskFailed"/>). Receives host events
/// through <see cref="ISharpClawEventSink"/>.
/// <para>
/// Moved out of <c>SharpClaw.Application.Core</c> by the trigger-extraction
/// plan; behavior is preserved verbatim.
/// </para>
/// </summary>
public sealed class TaskChainTriggerSource(
    ISharpClawEventSinkRegistry sinkRegistry,
    ILogger<TaskChainTriggerSource> logger) : ITaskTriggerSource, ISharpClawEventSink
{
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    public IReadOnlyList<string> TriggerKeys { get; } =
    [
        TaskScriptingTriggerKeys.TaskCompleted,
        TaskScriptingTriggerKeys.TaskFailed,
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
    public string? GetBindingValue(TaskTriggerDefinition def) =>
        def.Parameters.GetValueOrDefault(TaskScriptingTriggerKeys.SourceTaskName);

    // ── ISharpClawEventSink ───────────────────────────────────────

    public SharpClawEventType SubscribedEvents =>
        SharpClawEventType.JobCompleted | SharpClawEventType.JobFailed;

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
                    "TaskChainTriggerSource failed to fire context for definition {Id}.",
                    ctx.TaskDefinitionId);
            }
        }
    }

    private static bool MatchesContext(ITaskTriggerSourceContext ctx, SharpClawEvent evt) =>
        ctx.Definition.TriggerKey switch
        {
            TaskScriptingTriggerKeys.TaskCompleted => evt.Type.HasFlag(SharpClawEventType.JobCompleted),
            TaskScriptingTriggerKeys.TaskFailed    => evt.Type.HasFlag(SharpClawEventType.JobFailed),
            _ => false,
        };
}
