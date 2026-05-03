namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Allows a module to extend the task script parser with additional
/// context-API step kinds and event-trigger handler names.
/// Implementations are registered once at startup via
/// <c>TaskScriptParser.RegisterModule</c>.
/// </summary>
public interface ITaskParserModuleExtension
{
    /// <summary>
    /// Maps context-API method names (as they appear in task scripts) to a
    /// module-owned step key and the owning module ID.
    /// The parser records the step key in <c>TaskStepDefinition.StepKey</c>.
    /// </summary>
    IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings { get; }

    /// <summary>
    /// Maps event-handler method names (as they appear in task scripts) to a
    /// module-owned trigger key and the owning module ID.
    /// The parser stores the key in <c>TaskStepDefinition.ModuleTriggerKey</c>.
    /// </summary>
    IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; }

    /// <summary>
    /// Method names in <see cref="StepKeyMappings"/> whose first argument
    /// should be captured as <c>Expression</c> on the parsed step.
    /// </summary>
    IReadOnlySet<string> SingleArgExpressionMethods { get; }

    /// <summary>
    /// Optional contribution of the wire-format step-key strings the parser
    /// stamps on statement-shaped constructs that have no method-name
    /// binding (declarations, assignments, control flow, return, delay,
    /// evaluate, log, parse-response).
    /// <para>
    /// Exactly one registered module must provide a non-null value.
    /// Core owns no statement step-key constants; the parser depends
    /// entirely on this contribution.
    /// </para>
    /// </summary>
    TaskParserPrimitives? Primitives => null;

    /// <summary>
    /// Maps trigger-attribute names (short form, e.g. <c>"Schedule"</c>) to
    /// module-owned handlers that emit a <see cref="TaskTriggerDefinition"/>
    /// for each matching attribute occurrence. The parser also accepts the
    /// <c>"…Attribute"</c> long form for the same handler. A registered
    /// handler returning <see langword="null"/> from
    /// <see cref="ITaskTriggerAttributeHandler.Handle"/> declines the
    /// attribute and the parser falls back to its built-in switch.
    /// <para>
    /// Phase 1 of the trigger-attribute module migration. No core
    /// attribute is moved out yet; the legacy switch in
    /// <c>TaskScriptParser</c> remains the source of truth for any
    /// attribute name that no module claims.
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers
        => new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal);
}

