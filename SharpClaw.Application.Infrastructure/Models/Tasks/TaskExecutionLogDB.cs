using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Tasks;

/// <summary>
/// A single timestamped log entry within a <see cref="TaskInstanceDB"/>.
/// Written by the task orchestrator when the script calls <c>Log()</c>
/// or when the engine emits lifecycle events.
/// </summary>
public class TaskExecutionLogDB : BaseEntity
{
    public Guid TaskInstanceId { get; set; }
    public TaskInstanceDB TaskInstance { get; set; } = null!;

    public required string Message { get; set; }

    /// <summary>"Info", "Warning", or "Error".</summary>
    public string Level { get; set; } = "Info";
}
