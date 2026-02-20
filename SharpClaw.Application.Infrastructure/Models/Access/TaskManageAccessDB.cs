using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Access;

/// <summary>
/// Grants a role edit access to a specific scheduled task.
/// </summary>
public class TaskManageAccessDB : BaseEntity
{
    /// <summary>
    /// Per-permission clearance override.
    /// <see cref="PermissionClearance.Unset"/> falls back to the group default.
    /// </summary>
    public PermissionClearance Clearance { get; set; } = PermissionClearance.Unset;

    public Guid PermissionSetId { get; set; }
    public PermissionSetDB PermissionSet { get; set; } = null!;

    public Guid ScheduledTaskId { get; set; }
    public ScheduledJobDB ScheduledTask { get; set; } = null!;
}
