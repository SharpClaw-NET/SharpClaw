using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Roles;

// ── Requests ──────────────────────────────────────────────────────

/// <summary>
/// Creates a new role with an empty permission set.
/// </summary>
public sealed record CreateRoleRequest(string Name);

/// <summary>
/// Replaces the entire permission set of a role. The calling user must
/// hold every permission they are granting — you cannot give what you
/// don't have.
/// <para>
/// Global flags are dictionary-keyed by FlagKey (e.g. "CanClickDesktop").
/// Presence means the flag is granted; the value is the per-flag clearance.
/// Per-resource grants are dictionary-keyed by ResourceType (e.g. "CuDisplay").
/// See Module-System-Design §12.4.5.
/// </para>
/// </summary>
public sealed record SetRolePermissionsRequest(

    /// <summary>
    /// Global flag grants. Key = FlagKey (e.g. "CanClickDesktop"),
    /// Value = clearance for that flag.
    /// Presence in the dictionary means the flag is granted (true).
    /// Absence means the flag is not granted (false).
    /// </summary>
    IReadOnlyDictionary<string, PermissionClearance>? GlobalFlags = null,

    /// <summary>
    /// Per-resource grants. Key = ResourceType (e.g. "DsShell", "CuDisplay"),
    /// Value = list of resource grants for that type.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<ResourceGrant>>? ResourceGrants = null);

/// <summary>
/// A single per-resource grant entry.
/// target resource GUID (or <see cref="WellKnownIds.AllResources"/>
/// for wildcard).
/// </summary>
public sealed record ResourceGrant(
    Guid ResourceId,
    PermissionClearance Clearance = PermissionClearance.Unset);

// ── Responses ─────────────────────────────────────────────────────

public sealed record RoleResponse(
    Guid Id,
    string Name,
    Guid? PermissionSetId);

/// <summary>
/// Full permission set for a role. Global flags are dictionary-keyed by
/// FlagKey; per-resource grants are dictionary-keyed by ResourceType.
/// See Module-System-Design §12.4.5.
/// </summary>
public sealed record RolePermissionsResponse(
    Guid RoleId,
    string RoleName,
    IReadOnlyDictionary<string, PermissionClearance> GlobalFlags,
    IReadOnlyDictionary<string, IReadOnlyList<ResourceGrant>> ResourceGrants);
