using SharpClaw.Contracts.Entities;
using SharpClaw.Infrastructure.Models;

namespace SharpClaw.Application.Infrastructure.Models.Clearance;

/// <summary>
/// A whitelisted user on a <see cref="PermissionSetDB"/> group.
/// Users on this list can approve agent actions at
/// <see cref="Contracts.Enums.PermissionClearance.ApprovedByWhitelistedUser"/>
/// even if they do not hold the permission themselves.
/// </summary>
public class ClearanceUserWhitelistEntryDB : BaseEntity
{
    public Guid PermissionSetId { get; set; }
    public PermissionSetDB PermissionSet { get; set; } = null!;

    public Guid UserId { get; set; }
    public UserDB User { get; set; } = null!;
}
