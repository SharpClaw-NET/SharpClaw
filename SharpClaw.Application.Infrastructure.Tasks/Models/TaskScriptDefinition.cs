namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// The complete parsed representation of a task script (.cs file).
/// Produced by the parser, consumed by the validator and compiler.
/// </summary>
public sealed record TaskScriptDefinition
{
    /// <summary>Task name from the <c>[Task("…")]</c> attribute.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description from the <c>[Description("…")]</c> attribute.</summary>
    public string? Description { get; init; }

    /// <summary>Raw source text of the .cs file.</summary>
    public required string SourceText { get; init; }

    /// <summary>Name of the main task class.</summary>
    public required string ClassName { get; init; }

    /// <summary>Name of the entry-point method (typically <c>RunAsync</c>).</summary>
    public required string EntryPointMethod { get; init; }

    /// <summary>Input parameters declared in the task class.</summary>
    public required IReadOnlyList<TaskParameterDefinition> Parameters { get; init; }

    /// <summary>Custom data types defined in the script.</summary>
    public required IReadOnlyList<TaskDataTypeDefinition> DataTypes { get; init; }

    /// <summary>
    /// The primary output type that <see cref="TaskStepKind.Emit"/> pushes
    /// to listeners.  <see langword="null"/> if the task has no structured output.
    /// </summary>
    public TaskDataTypeDefinition? OutputType { get; init; }

    /// <summary>Ordered steps in the task entry-point body.</summary>
    public required IReadOnlyList<TaskStepDefinition> Steps { get; init; }

    /// <summary>
    /// Custom tool-call hooks defined via <c>[ToolCall("name")]</c>
    /// methods in the task script.  Each hook becomes a tool that
    /// agents can invoke during chat steps.
    /// </summary>
    public IReadOnlyList<TaskToolCallHook> ToolCallHooks { get; init; } = [];

    /// <summary>
    /// Agent output format annotation from <c>[AgentOutput("format")]</c>
    /// on the task class.  When non-null, agents may write structured
    /// results to the task via the <c>task_output</c> tool.
    /// </summary>
    public string? AgentOutputFormat { get; init; }
}
