namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// A single step in a task script body.  The <see cref="Kind"/>
/// discriminator determines which properties are relevant.  Steps form
/// a tree: event handlers, conditionals, and loops contain nested body
/// steps.
/// </summary>
public sealed record TaskStepDefinition
{
    /// <summary>What this step does.</summary>
    public required TaskStepKind Kind { get; init; }

    /// <summary>Source line number (1-based) for diagnostics.</summary>
    public required int Line { get; init; }

    /// <summary>Source column (0-based) for diagnostics.</summary>
    public required int Column { get; init; }

    // ── Identifiers ───────────────────────────────────────────────

    /// <summary>
    /// Variable name for <see cref="TaskStepKind.DeclareVariable"/>
    /// and <see cref="TaskStepKind.Assign"/>.
    /// </summary>
    public string? VariableName { get; init; }

    /// <summary>
    /// Type name for <see cref="TaskStepKind.DeclareVariable"/>,
    /// <see cref="TaskStepKind.ParseResponse"/>, and object creation.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Variable that stores the result of this step.  Used by steps
    /// that produce a value (Chat, StartTranscription, ParseResponse …).
    /// </summary>
    public string? ResultVariable { get; init; }

    // ── Expressions ───────────────────────────────────────────────

    /// <summary>
    /// Expression text whose interpretation depends on <see cref="Kind"/>:
    /// DeclareVariable (initialiser), Assign (value), Chat (message),
    /// Conditional (condition), Loop (condition), Delay (duration),
    /// Log (message), Evaluate (expression), HttpRequest (URL).
    /// </summary>
    public string? Expression { get; init; }

    // ── Arguments ─────────────────────────────────────────────────

    /// <summary>
    /// Positional arguments: variable references or literal values
    /// passed to context-API steps (StartTranscription, Emit, etc.).
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    // ── Event handler ─────────────────────────────────────────────

    /// <summary>
    /// Trigger kind for <see cref="TaskStepKind.EventHandler"/>.
    /// </summary>
    public TaskTriggerKind? TriggerKind { get; init; }

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

    /// <summary>
    /// Else branch for <see cref="TaskStepKind.Conditional"/>.
    /// </summary>
    public IReadOnlyList<TaskStepDefinition>? ElseBody { get; init; }

    /// <summary>
    /// Specific loop shape for <see cref="TaskStepKind.Loop"/>.
    /// </summary>
    public TaskLoopKind? LoopKind { get; init; }

    // ── HTTP ──────────────────────────────────────────────────────

    /// <summary>
    /// HTTP verb for <see cref="TaskStepKind.HttpRequest"/>
    /// ("GET", "POST", "PUT", "DELETE").
    /// </summary>
    public string? HttpMethod { get; init; }
}
