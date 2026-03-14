using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Tasks;

/// <summary>
/// A single execution of a <see cref="TaskDefinitionDB"/>.  Tracks
/// lifecycle status, parameter values, latest emitted output, and
/// timing information.
/// </summary>
public class TaskInstanceDB : BaseEntity
{
    // ── Definition ────────────────────────────────────────────────

    public Guid TaskDefinitionId { get; set; }
    public TaskDefinitionDB TaskDefinition { get; set; } = null!;

    // ── Execution state ──────────────────────────────────────────

    public TaskInstanceStatus Status { get; set; } = TaskInstanceStatus.Queued;

    /// <summary>
    /// JSON-serialised dictionary of parameter name → value supplied
    /// when the instance was created.
    /// </summary>
    public string? ParameterValuesJson { get; set; }

    /// <summary>
    /// JSON snapshot of the last object passed to <c>Emit()</c>.
    /// Updated on every emission so listeners that connect late
    /// can receive the current state.
    /// </summary>
    public string? OutputSnapshotJson { get; set; }

    /// <summary>Error message when <see cref="Status"/> is <see cref="TaskInstanceStatus.Failed"/>.</summary>
    public string? ErrorMessage { get; set; }

    // ── Shared data snapshots ────────────────────────────────────

    /// <summary>
    /// Latest light shared data text written by agents during execution.
    /// Updated on every write for debug/log purposes.
    /// </summary>
    public string? LightDataSnapshot { get; set; }

    /// <summary>
    /// JSON snapshot of all big shared data entries.  Updated on every
    /// write/remove for debug/log purposes.  Format:
    /// <c>[{ "id": "...", "title": "...", "content": "...", "createdAt": "..." }, ...]</c>
    /// </summary>
    public string? BigDataSnapshotJson { get; set; }

    // ── Timing ───────────────────────────────────────────────────

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // ── Caller ───────────────────────────────────────────────────

    /// <summary>The user who started this instance (null if started by an agent).</summary>
    public Guid? CallerUserId { get; set; }

    /// <summary>The agent that started this instance (null if started by a user).</summary>
    public Guid? CallerAgentId { get; set; }

    // ── Channel ──────────────────────────────────────────────────

    /// <summary>
    /// Optional channel used for <c>Chat</c> / <c>ChatStream</c> operations
    /// within the task entry point.
    /// </summary>
    public Guid? ChannelId { get; set; }
    public ChannelDB? Channel { get; set; }

    // ── Navigation ────────────────────────────────────────────────

    public ICollection<TaskExecutionLogDB> LogEntries { get; set; } = [];
    public ICollection<TaskOutputEntryDB> OutputEntries { get; set; } = [];
}
