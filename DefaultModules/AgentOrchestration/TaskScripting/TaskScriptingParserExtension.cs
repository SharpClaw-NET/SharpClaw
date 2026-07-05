using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Registers Agent Orchestration event-handler names and trigger attributes
/// with the task parser. C# task-language statements are Core-owned.
/// </summary>
public sealed class TaskScriptingParserExtension : ITaskParserModuleExtension
{
    public static readonly TaskScriptingParserExtension Instance = new();

    /// <summary>
    /// Stable trigger key recorded on the parsed statement's
    /// <c>ModuleTriggerKey</c> for <c>OnTimer</c> handlers.
    /// </summary>
    public const string TimerTriggerKey = "sharpclaw.task_scripting.timer";

    public IReadOnlyDictionary<string, (string OperationKey, string ModuleId)> OperationKeyMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["OnTimer"] = (TimerTriggerKey, "sharpclaw_agent_orchestration"),
        };

    public IReadOnlySet<string> SingleArgExpressionMethods { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Trigger-attribute handlers owned by this module. Phase 2 of the
    /// trigger-attribute migration: <c>[Schedule]</c>, <c>[OnStartup]</c>,
    /// <c>[OnShutdown]</c>, <c>[OnTaskCompleted]</c>, <c>[OnTaskFailed]</c>,
    /// and <c>[OnTrigger]</c> are claimed here. The parser routes matching
    /// attribute occurrences through these handlers before falling back to
    /// its built-in switch.
    /// </summary>
    public IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers { get; } =
        AgentOrchestrationTriggerAttributeHandlers.All;

}
