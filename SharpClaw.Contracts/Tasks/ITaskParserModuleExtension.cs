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
    /// The parser records <see cref="TaskTriggerKind.ModuleEvent"/> on the
    /// step and stores the key in <c>TaskStepDefinition.ModuleTriggerKey</c>.
    /// </summary>
    IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; }

    /// <summary>
    /// Method names in <see cref="StepKeyMappings"/> whose first argument
    /// should be captured as <c>Expression</c> on the parsed step.
    /// </summary>
    IReadOnlySet<string> SingleArgExpressionMethods { get; }
}

