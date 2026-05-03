using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// A single step in a task script body.  The <see cref="StepKey"/>
/// discriminator determines which properties are relevant.  Steps form
/// a tree: event handlers, conditionals, and loops contain nested body
/// steps.
/// </summary>
public sealed record TaskStepDefinition : ITaskStepInvocation
{
    /// <summary>
    /// Stable wire-format string key identifying this step's operation
    /// (e.g. <c>core.chat</c>). Step keys are owned by modules and exposed
    /// through module-local constant classes (for example
    /// <c>TaskScriptingStepKeys</c>, <c>AgentOrchestrationStepKeys</c>, and
    /// <c>HttpStepKeys</c>); the literal values are stable across versions
    /// for backward compatibility with serialized task scripts.
    /// </summary>
    public required string StepKey { get; init; }

    /// <summary>Source line number (1-based) for diagnostics.</summary>
    public required int Line { get; init; }

    /// <summary>Source column (0-based) for diagnostics.</summary>
    public required int Column { get; init; }

    // ── Identifiers ───────────────────────────────────────────────

    /// <summary>
    /// Variable name for <c>core.declare_variable</c> and <c>core.assign</c> steps.
    /// </summary>
    public string? VariableName { get; init; }

    /// <summary>
    /// Type name for declare-variable, parse-response, and object creation steps.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Variable that stores the result of this step.  Used by steps
    /// that produce a value (Chat, Emit, ParseResponse …).
    /// </summary>
    public string? ResultVariable { get; init; }

    // ── Expressions ───────────────────────────────────────────────

    /// <summary>
    /// Expression text whose interpretation depends on <see cref="StepKey"/>:
    /// DeclareVariable (initialiser), Assign (value), Chat (message),
    /// Conditional (condition), Loop (condition), Delay (duration),
    /// Log (message), Evaluate (expression), HttpRequest (URL).
    /// </summary>
    public string? Expression { get; init; }

    // ── Arguments ─────────────────────────────────────────────────

    /// <summary>
    /// Positional arguments: variable references or literal values
    /// passed to context-API steps (Chat, Emit, module steps, etc.).
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    // ── Event handler ─────────────────────────────────────────────

    /// <summary>
    /// Module-owned trigger key for <c>core.event_handler</c> steps.
    /// Identifies which module trigger the handler is bound to.
    /// </summary>
    public string? ModuleTriggerKey { get; init; }

    /// <summary>
    /// Lambda parameter name for event-handler callbacks.
    /// </summary>
    public string? HandlerParameter { get; init; }

    // ── Nesting ───────────────────────────────────────────────────

    /// <summary>
    /// Nested steps: event-handler body, conditional then-branch,
    /// or loop body.
    /// </summary>
    public IReadOnlyList<TaskStepDefinition>? Body { get; init; }

    /// <summary>Else branch for <c>core.conditional</c> steps.</summary>
    public IReadOnlyList<TaskStepDefinition>? ElseBody { get; init; }

    // ── ITaskStepInvocation projection ────────────────────────────

    string? ITaskStepInvocation.RawExpression => Expression;
    IReadOnlyList<ITaskStepInvocation>? ITaskStepInvocation.Body => Body;
    IReadOnlyList<ITaskStepInvocation>? ITaskStepInvocation.ElseBody => ElseBody;
}
