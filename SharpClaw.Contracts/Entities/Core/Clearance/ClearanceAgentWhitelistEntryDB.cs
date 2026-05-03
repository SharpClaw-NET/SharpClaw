using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Entities.Core;

namespace SharpClaw.Contracts.Entities.Core.Clearance;

/// <summary>
/// A whitelisted agent on a <see cref="PermissionSetDB"/> group.
/// Agents on this list can approve other agents' actions at
/// <see cref="Contracts.Enums.PermissionClearance.ApprovedByWhitelistedAgent"/>.
/// </summary>
public class ClearanceAgentWhitelistEntryDB : BaseEntity
{
    public Guid PermissionSetId { get; set; }
    public PermissionSetDB PermissionSet { get; set; } = null!;

    public Guid AgentId { get; set; }
    public AgentDB Agent { get; set; } = null!;
}
