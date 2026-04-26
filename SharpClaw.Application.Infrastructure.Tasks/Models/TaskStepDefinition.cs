using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// A single step in a task script body.  The <see cref="StepKey"/>
/// discriminator determines which properties are relevant.  Steps form
/// a tree: event handlers, conditionals, and loops contain nested body
/// steps.
/// </summary>
public sealed record TaskStepDefinition
{
    /// <summary>
    /// Stable string key identifying this step's operation (e.g. <c>core.chat</c>).
    /// Use <see cref="SharpClaw.Contracts.Tasks.WellKnownTaskStepKeys"/> constants
    /// for core steps.  Module steps use a module-namespaced key.
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

    /// <summary>Trigger kind for <c>core.event_handler</c> steps.</summary>
    public TaskTriggerKind? TriggerKind { get; init; }

    /// <summary>
    /// Module-owned trigger key when <see cref="TriggerKind"/> is
    /// <see cref="TaskTriggerKind.ModuleEvent"/>.
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

    /// <summary>Specific loop shape for <c>core.loop</c> steps.</summary>
    public TaskLoopKind? LoopKind { get; init; }

    // ── HTTP ──────────────────────────────────────────────────────

    /// <summary>
    /// HTTP verb for <c>core.http_request</c> steps
    /// ("GET", "POST", "PUT", "DELETE").
    /// </summary>
    public string? HttpMethod { get; init; }
}
