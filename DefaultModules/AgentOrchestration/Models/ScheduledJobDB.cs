using SharpClaw.Contracts.Entities;
using SharpClaw.Modules.AgentOrchestration.ScheduledJobs;

namespace SharpClaw.Modules.AgentOrchestration.Models;

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

    // FK to host TaskDefinitionDB — bare Guid, no nav property
    public Guid? TaskDefinitionId { get; set; }

    /// <summary>
    /// JSON-serialised <c>Dictionary&lt;string, string&gt;</c> of parameter
    /// values forwarded to each created instance.
    /// </summary>
    public string? ParameterValuesJson { get; set; }

    /// <summary>Optional agent recorded as the caller on created instances.</summary>
    public Guid? CallerAgentId { get; set; }

    // FK to host ChannelContextDB — bare Guid, no nav property
    public Guid? AgentContextId { get; set; }

    // FK to host PermissionSetDB — bare Guid, no nav property
    public Guid? PermissionSetId { get; set; }

    public string? CronExpression { get; set; }
    public string? CronTimezone { get; set; }
    public MissedFirePolicy MissedFirePolicy { get; set; } = MissedFirePolicy.FireOnceAndRecompute;
}
