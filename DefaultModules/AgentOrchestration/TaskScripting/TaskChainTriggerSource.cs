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
/// Moved out of <c>SharpClaw.Runtime.BLL</c> by the trigger-extraction
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

    // TODO: JobCompleted / JobFailed flags were removed from
    // SharpClawEventType because the host never raised them. Pick a real
    // signal (or a module-defined event source) for task-chain triggers.
    public SharpClawEventType SubscribedEvents => SharpClawEventType.None;

    public Task OnEventAsync(SharpClawEvent evt, CancellationToken ct) =>
        Task.CompletedTask;
}
