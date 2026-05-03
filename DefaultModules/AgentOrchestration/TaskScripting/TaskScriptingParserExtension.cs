using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Registers the Task Scripting module's event-handler names with the parser.
/// The lifecycle <c>OnTimer</c> handler is contributed here as a module-owned
/// trigger; the parser stores the trigger key on
/// <c>TaskStepDefinition.ModuleTriggerKey</c>.
/// </summary>
public sealed class TaskScriptingParserExtension : ITaskParserModuleExtension
{
    public static readonly TaskScriptingParserExtension Instance = new();

    /// <summary>
    /// Stable trigger key recorded on the parsed step's
    /// <c>ModuleTriggerKey</c> for <c>OnTimer</c> handlers.
    /// </summary>
    public const string TimerTriggerKey = "sharpclaw.task_scripting.timer";

    public IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["OnTimer"] = (TimerTriggerKey, "sharpclaw_agent_orchestration"),
        };

    public IReadOnlySet<string> SingleArgExpressionMethods { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Wire-format step-key strings for the statement-shaped scripting
    /// primitives the parser emits directly. Sourced from
    /// <see cref="TaskScriptingStepKeys"/> so the module is the single
    /// source of truth; core defines no statement step-key constants.
    /// </summary>
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

    public TaskParserPrimitives? Primitives { get; } = new()
    {
        DeclareVariable = TaskScriptingStepKeys.DeclareVariable,
        Assign          = TaskScriptingStepKeys.Assign,
        EventHandler    = TaskScriptingStepKeys.EventHandler,
        Conditional     = TaskScriptingStepKeys.Conditional,
        Loop            = TaskScriptingStepKeys.Loop,
        Return          = TaskScriptingStepKeys.Return,
        Delay           = TaskScriptingStepKeys.Delay,
        Evaluate        = TaskScriptingStepKeys.Evaluate,
        Log             = TaskScriptingStepKeys.Log,
        ParseResponse   = TaskScriptingStepKeys.ParseResponse,
    };
}
