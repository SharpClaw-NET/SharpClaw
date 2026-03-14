using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Tasks;

// ── Requests ──────────────────────────────────────────────────────

/// <summary>
/// Register a new task definition from raw .cs source.
/// </summary>
public sealed record CreateTaskDefinitionRequest(
    string SourceText);

/// <summary>
/// Update an existing task definition's source or active flag.
/// </summary>
public sealed record UpdateTaskDefinitionRequest(
    string? SourceText = null,
    bool? IsActive = null);

/// <summary>
/// Start a new instance of a task definition.
/// </summary>
public sealed record StartTaskInstanceRequest(
    Guid TaskDefinitionId,
    Guid? ChannelId = null,
    Dictionary<string, string>? ParameterValues = null);

// ── Responses ─────────────────────────────────────────────────────

public sealed record TaskDefinitionResponse(
    Guid Id,
    string Name,
    string? Description,
    string? OutputTypeName,
    bool IsActive,
    IReadOnlyList<TaskParameterResponse> Parameters,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CustomId = null);

public sealed record TaskParameterResponse(
    string Name,
    string TypeName,
    string? Description,
    string? DefaultValue,
    bool IsRequired);

public sealed record TaskInstanceResponse(
    Guid Id,
    Guid TaskDefinitionId,
    string TaskName,
    TaskInstanceStatus Status,
    string? OutputSnapshotJson,
    string? ErrorMessage,
    IReadOnlyList<TaskExecutionLogResponse> Logs,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record TaskInstanceSummaryResponse(
    Guid Id,
    Guid TaskDefinitionId,
    string TaskName,
    TaskInstanceStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record TaskExecutionLogResponse(
    string Message,
    string Level,
    DateTimeOffset Timestamp);

public sealed record TaskOutputEntryResponse(
    Guid Id,
    long Sequence,
    string? Data,
    DateTimeOffset Timestamp);

// ── Streaming ─────────────────────────────────────────────────────

/// <summary>
/// A single event pushed to SSE / WebSocket listeners.
/// The task has full control over <see cref="Data"/>: it decides
/// when to emit, how often, and what format the payload takes.
/// </summary>
public sealed record TaskOutputEvent(
    TaskOutputEventType Type,
    long Sequence,
    DateTimeOffset Timestamp,
    /// <summary>
    /// Arbitrary payload produced by the task's <c>Emit(...)</c> call.
    /// May be a JSON object, plain text, or null for lifecycle events.
    /// </summary>
    string? Data);

public enum TaskOutputEventType
{
    /// <summary>Task-emitted output (from <c>Emit(...)</c>).</summary>
    Output,

    /// <summary>Log message appended during execution.</summary>
    Log,

    /// <summary>Task status changed (started, completed, failed, etc.).</summary>
    StatusChange,

    /// <summary>Terminal event — no more events will follow.</summary>
    Done
}
