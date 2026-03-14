namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// A custom data type defined inside a task script.  Data types define
/// the shape of structured results that agents populate and tasks emit
/// to listeners.
/// </summary>
public sealed record TaskDataTypeDefinition(
    string Name,
    IReadOnlyList<TaskPropertyDefinition> Properties,
    /// <summary>
    /// When <see langword="true"/> this type is the primary output schema
    /// pushed to SSE / WebSocket listeners via <see cref="TaskStepKind.Emit"/>.
    /// </summary>
    bool IsOutputType = false);
