namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// A custom tool hook defined in a task script via the
/// <c>[ToolCall("name")]</c> attribute on a method.  When an agent
/// invokes the tool during chat, the orchestrator executes the
/// <see cref="Body"/> steps and returns the result.
/// </summary>
public sealed record TaskToolCallHook
{
    /// <summary>
    /// The tool name as it appears to the model
    /// (e.g. <c>"summarize_context"</c>).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional human-readable description shown in the tool schema.
    /// Extracted from a <c>[Description("…")]</c> attribute on the method.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Named parameters the model must supply when calling this tool.
    /// Each entry maps a parameter name to its type name.
    /// </summary>
    public required IReadOnlyList<TaskToolCallParameter> Parameters { get; init; }

    /// <summary>
    /// The compiled body steps executed when the tool is invoked.
    /// </summary>
    public required IReadOnlyList<TaskStepDefinition> Body { get; init; }

    /// <summary>
    /// The variable name whose value is returned as the tool result
    /// when execution completes.  Defaults to <c>"$return"</c>.
    /// </summary>
    public string ReturnVariable { get; init; } = "$return";
}

/// <summary>
/// A single parameter declared on a <see cref="TaskToolCallHook"/>.
/// </summary>
public sealed record TaskToolCallParameter(
    string Name,
    string TypeName,
    string? Description = null);
