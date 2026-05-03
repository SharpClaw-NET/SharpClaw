namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Module-owned recogniser for a single trigger attribute name (the short
/// form, e.g. <c>"Schedule"</c>; the parser also accepts the
/// <c>"ScheduleAttribute"</c> long form via the registration map).
/// <para>
/// Attribute classes are not real .NET types in task scripts — scripts are
/// parsed but not compiled — so attribute ownership cannot be expressed as
/// type containment. Instead, a module registers a handler under each
/// attribute name it claims; the parser routes matching attribute
/// occurrences to the handler and uses the returned
/// <see cref="TaskTriggerDefinition"/> directly.
/// </para>
/// </summary>
public interface ITaskTriggerAttributeHandler
{
    /// <summary>
    /// Inspect a single attribute occurrence and return its parsed
    /// <see cref="TaskTriggerDefinition"/>, or <see langword="null"/> to
    /// decline (in which case the parser falls back to its built-in switch).
    /// Handlers may emit diagnostics via
    /// <see cref="TaskTriggerAttributeContext.Report"/>.
    /// </summary>
    TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context);
}
