using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Jobs;

public class ScheduledJobDB : BaseEntity
{
    public required string Name { get; set; }
    public required DateTimeOffset NextRunAt { get; set; }
    public TimeSpan? RepeatInterval { get; set; }
    public int MaxRetries { get; set; } = 3;
    public int RetryCount { get; set; }
    public ScheduledTaskStatus Status { get; set; } = ScheduledTaskStatus.Pending;
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Optional context this task belongs to.  When set, the context's
    /// permission set acts as a default for this task.
    /// </summary>
    public Guid? AgentContextId { get; set; }
    public AgentContextDB? AgentContext { get; set; }

    /// <summary>Optional per-task permission set override.</summary>
    public Guid? PermissionSetId { get; set; }
    public PermissionSetDB? PermissionSet { get; set; }
}

public enum ScheduledTaskStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
