using SharpClaw.Contracts.DTOs.Chat;
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
/// Either <see cref="ChannelId"/> or <see cref="ContextId"/> must be supplied.
/// When only <see cref="ContextId"/> is provided the task is expected to call
/// <c>CreateChannel</c> early in its body to establish its own channel; that
/// channel is automatically associated with the supplied context.
/// </summary>
public sealed record StartTaskInstanceRequest(
    Guid TaskDefinitionId,
    Guid? ChannelId = null,
    Dictionary<string, string>? ParameterValues = null,
    bool StartImmediately = false,
    Guid? ContextId = null);

/// <summary>
/// Validate task definition source without persisting it.
/// </summary>
public sealed record ValidateTaskDefinitionRequest(
    string SourceText);

// ── Responses ─────────────────────────────────────────────────────

public sealed record TaskDefinitionResponse(
    Guid Id,
    string Name,
    string? Description,
    string? OutputTypeName,
    bool IsActive,
    IReadOnlyList<TaskParameterResponse> Parameters,
    IReadOnlyList<TaskRequirementResponse> Requirements,
    IReadOnlyList<TaskTriggerResponse> Triggers,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CustomId = null);

public sealed record TaskParameterResponse(
    string Name,
    string TypeName,
    string? Description,
    string? DefaultValue,
    bool IsRequired);

/// <summary>
/// A single environment requirement declared on a task definition,
/// surfaced so callers can check prerequisites before starting an instance.
/// </summary>
public sealed record TaskRequirementResponse(
    string Kind,
    string Severity,
    string? Value,
    string? CapabilityValue,
    string? ParameterName);

/// <summary>
/// A single self-registration trigger binding surfaced on a task definition response.
/// </summary>
public sealed record TaskTriggerResponse(
    string Kind,
    string? TriggerValue,
    string? Filter,
    bool IsEnabled);

/// <summary>
/// A registered trigger source exposed to clients for discovery.
/// </summary>
public sealed record TaskTriggerSourceResponse(
    string? SourceName,
    IReadOnlyList<string> SupportedKinds,
    string Type,
    bool IsCustom);

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
    DateTimeOffset? CompletedAt,
    Guid? ChannelId = null,
    ChannelCostResponse? ChannelCost = null,
    Guid? ContextId = null);

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

public sealed record TaskValidationResponse(
    bool IsValid,
    IReadOnlyList<TaskDiagnosticResponse> Diagnostics);

/// <summary>
/// The aggregated outcome of a task preflight check, as returned by
/// <c>GET /tasks/{id}/preflight</c>.
/// </summary>
public sealed record TaskPreflightResponse(
    bool IsBlocked,
    IReadOnlyList<TaskPreflightFindingResponse> Findings);

/// <summary>
/// A single finding from a task preflight check.
/// </summary>
public sealed record TaskPreflightFindingResponse(
    string RequirementKind,
    string Severity,
    bool Passed,
    string Message,
    string? ParameterName = null);

public sealed record TaskDiagnosticResponse(
    string Severity,
    string Code,
    string Message,
    int Line,
    int Column);

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
