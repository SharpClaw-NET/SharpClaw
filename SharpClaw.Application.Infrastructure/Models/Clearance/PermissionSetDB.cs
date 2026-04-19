using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Clearance;

/// <summary>
/// A reusable set of permissions that can be attached to a role, context,
/// or channel. Defines what actions the holder is permitted to
/// perform and at what clearance level.
/// </summary>
public class PermissionSetDB : BaseEntity
{
    // ── Per-resource grant collections ────────────────────

    /// <summary>
    /// All global boolean flag grants for this permission set.
    /// Filtered by <see cref="GlobalFlagDB.FlagKey"/> at query time.
    /// See Module-System-Design §12.4.2.
    /// </summary>
    public ICollection<GlobalFlagDB> GlobalFlags { get; set; } = [];

    /// <summary>
    /// All per-resource permission grants for this permission set.
    /// Filtered by <see cref="ResourceAccessDB.ResourceType"/> at query time.
    /// See Module-System-Design §3.10.4.
    /// </summary>
    public ICollection<ResourceAccessDB> ResourceAccesses { get; set; } = [];

    // ── Clearance whitelists ──────────────────────────────────────

    /// <summary>
    /// Users who can approve agent actions at
    /// <see cref="PermissionClearance.ApprovedByWhitelistedUser"/>.
    /// </summary>
    public ICollection<ClearanceUserWhitelistEntryDB> ClearanceUserWhitelist { get; set; } = [];

    /// <summary>
    /// Agents who can approve other agents' actions at
    /// <see cref="PermissionClearance.ApprovedByWhitelistedAgent"/>.
    /// </summary>
    public ICollection<ClearanceAgentWhitelistEntryDB> ClearanceAgentWhitelist { get; set; } = [];
}
