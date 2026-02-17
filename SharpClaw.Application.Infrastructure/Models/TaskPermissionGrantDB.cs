using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Infrastructure.Models;

/// <summary>
/// A permission clearance level pre-approved for a specific action type
/// on a scheduled task. Overrides the context-level default when present.
/// </summary>
public class TaskPermissionGrantDB : BaseEntity
{
    /// <summary>The action type this grant applies to.</summary>
    public AgentActionType ActionType { get; set; }

    /// <summary>
    /// The maximum clearance level the user pre-approves for this action
    /// type on this task.
    /// </summary>
    public PermissionClearance GrantedClearance { get; set; }

    public Guid ScheduledTaskId { get; set; }
    public ScheduledTaskDB ScheduledTask { get; set; } = null!;
}
