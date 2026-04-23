using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Tasks;
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

    // ── Task definition binding ─────────────────────────────────────

    /// <summary>
    /// The task definition to launch when this scheduled job fires.
    /// When null the job is a legacy stub with no task dispatch.
    /// </summary>
    public Guid? TaskDefinitionId { get; set; }
    public TaskDefinitionDB? TaskDefinition { get; set; }

    /// <summary>
    /// JSON-serialised <c>Dictionary&lt;string, string&gt;</c> of parameter
    /// values forwarded to each created instance.  Null means no parameters.
    /// </summary>
    public string? ParameterValuesJson { get; set; }

    /// <summary>
    /// Optional agent ID recorded as the caller on created instances,
    /// used for audit and permission attribution.
    /// </summary>
    public Guid? CallerAgentId { get; set; }

    // ── Context / permissions ───────────────────────────────────────

    /// <summary>
    /// Optional context this task belongs to.  When set, the context's
    /// permission set acts as a default for this task.
    /// </summary>
    public Guid? AgentContextId { get; set; }
    public ChannelContextDB? AgentContext { get; set; }

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
