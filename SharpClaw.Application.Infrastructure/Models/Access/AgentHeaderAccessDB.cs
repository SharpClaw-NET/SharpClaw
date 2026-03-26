using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;

namespace SharpClaw.Application.Infrastructure.Models.Access;

/// <summary>
/// Grants a role the ability to edit the custom chat header of a specific agent.
/// </summary>
public class AgentHeaderAccessDB : BaseEntity
{
    /// <summary>
    /// Per-permission clearance override.
    /// <see cref="PermissionClearance.Unset"/> falls back to the group default.
    /// </summary>
    public PermissionClearance Clearance { get; set; } = PermissionClearance.Unset;

    public Guid PermissionSetId { get; set; }
    public PermissionSetDB PermissionSet { get; set; } = null!;

    public Guid AgentId { get; set; }
    public AgentDB Agent { get; set; } = null!;
}
