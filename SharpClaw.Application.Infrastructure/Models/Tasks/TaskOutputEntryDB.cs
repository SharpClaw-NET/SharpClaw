using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Tasks;

/// <summary>
/// A single persisted output emitted by a task instance via <c>Emit()</c>.
/// Each call produces one entry, providing a full history of outputs
/// that can be queried after the fact (unlike the live SSE stream).
/// </summary>
public class TaskOutputEntryDB : BaseEntity
{
    public Guid TaskInstanceId { get; set; }
    public TaskInstanceDB TaskInstance { get; set; } = null!;

    /// <summary>Monotonic sequence number within the instance.</summary>
    public long Sequence { get; set; }

    /// <summary>
    /// JSON payload from the task's <c>Emit(...)</c> call.
    /// The task has full control over format and content.
    /// </summary>
    public string? Data { get; set; }
}
