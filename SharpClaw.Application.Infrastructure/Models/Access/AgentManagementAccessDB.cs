using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;

namespace SharpClaw.Application.Infrastructure.Models.Access;

/// <summary>
/// Grants a role CRUD access to a specific agent (sub-agent management).
/// Sub-agents can only be created with permissions ≤ the creator's own.
/// </summary>
public class AgentManagementAccessDB : BaseEntity
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
