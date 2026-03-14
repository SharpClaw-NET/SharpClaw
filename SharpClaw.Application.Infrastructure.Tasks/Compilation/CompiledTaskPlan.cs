using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Application.Infrastructure.Tasks.Compilation;

/// <summary>
/// A compiled, executable task plan ready for the orchestrator.
/// Produced by <see cref="TaskScriptCompiler.Compile"/>.
/// </summary>
public sealed record CompiledTaskPlan
{
    public required string TaskName { get; init; }
    public string? Description { get; init; }
    public required TaskScriptDefinition Definition { get; init; }

    /// <summary>
    /// Resolved parameter values (name → JSON value).
    /// Populated at instantiation time from user input or defaults.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> ParameterValues { get; init; }

    /// <summary>
    /// The compiled entry-point body ready for execution.
    /// </summary>
    public required IReadOnlyList<TaskStepDefinition> ExecutionSteps { get; init; }

    /// <summary>
    /// Custom tool-call hooks defined via <c>[ToolCall("name")]</c>
    /// methods in the task script.  Empty when the task defines none.
    /// </summary>
    public IReadOnlyList<TaskToolCallHook> ToolCallHooks { get; init; } = [];

    /// <summary>
    /// Agent output format annotation from <c>[AgentOutput("format")]</c>.
    /// When non-null, agents may write results to the task via the
    /// <c>task_output</c> tool using this format.  When <c>null</c>,
    /// the <c>task_output</c> tool is not exposed.
    /// </summary>
    public string? AgentOutputFormat { get; init; }
}
