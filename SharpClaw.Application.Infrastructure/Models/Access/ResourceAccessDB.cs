using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Access;

/// <summary>
/// A unified per-resource permission grant. Each row grants an agent (via its
/// <see cref="PermissionSetDB"/>) access to a specific resource identified by
/// <see cref="ResourceType"/> and <see cref="ResourceId"/>.
/// <para>
/// Replaces the 19 separate typed access entities (e.g.
/// <c>DangerousShellAccessDB</c>, <c>ContainerAccessDB</c>) with a single
/// table that uses a <see cref="ResourceType"/> string discriminator.
/// See Module-System-Design §3.10.
/// </para>
/// </summary>
public class ResourceAccessDB : BaseEntity
{
    /// <summary>
    /// Discriminator that identifies the resource category.
    /// Must be one of the constants in <see cref="ResourceTypes"/>.
    /// </summary>
    public required string ResourceType { get; set; }

    /// <summary>
    /// The ID of the specific resource being granted access to,
    /// or <see cref="WellKnownIds.AllResources"/> for wildcard grants.
    /// </summary>
    public Guid ResourceId { get; set; }

    /// <summary>
    /// Per-grant clearance override.
    /// <see cref="PermissionClearance.Unset"/> falls back to the group default.
    /// </summary>
    public PermissionClearance Clearance { get; set; } = PermissionClearance.Unset;

    public Guid PermissionSetId { get; set; }
    public PermissionSetDB PermissionSet { get; set; } = null!;

    /// <summary>
    /// Optional sub-type discriminator for resource types that have variants.
    /// <para>
    /// Used by shell grants: <c>DsShell</c> stores a <see cref="DangerousShellType"/>
    /// string ("Bash", "PowerShell", etc.); <c>Mk8Shell</c> stores a
    /// <see cref="SafeShellType"/> string ("Mk8Shell").
    /// </para>
    /// Empty string for resource types without variants (not null — ensures
    /// deterministic unique-index behavior across all database providers).
    /// </summary>
    public string SubType { get; set; } = "";

    /// <summary>
    /// Optional access-level qualifier for tiered access.
    /// <para>
    /// Used by database grants: <c>DbInternal</c> and <c>DbExternal</c> store
    /// an access level ("ReadOnly", "ReadWrite", "Admin").
    /// </para>
    /// Null for resource types without tiered access.
    /// </summary>
    public string? AccessLevel { get; set; }

    /// <summary>
    /// When <c>true</c>, this grant is the default resource for its
    /// <see cref="ResourceType"/> within the owning <see cref="PermissionSetDB"/>.
    /// Replaces the 16 individual default FK pairs that were on PermissionSetDB.
    /// At most one grant per (PermissionSetId, ResourceType) may be marked default.
    /// </summary>
    public bool IsDefault { get; set; }
}
