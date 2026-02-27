using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Roles;

// ── Requests ──────────────────────────────────────────────────────

/// <summary>
/// Replaces the entire permission set of a role. The calling user must
/// hold every permission they are granting — you cannot give what you
/// don't have.
/// </summary>
public sealed record SetRolePermissionsRequest(
    PermissionClearance DefaultClearance = PermissionClearance.Unset,

    // Global flags
    bool CanCreateSubAgents = false,
    bool CanCreateContainers = false,
    bool CanRegisterInfoStores = false,
    bool CanAccessLocalhostInBrowser = false,
    bool CanAccessLocalhostCli = false,

    // Per-resource grants
    IReadOnlyList<ResourceGrant>? DangerousShellAccesses = null,
    IReadOnlyList<ResourceGrant>? SafeShellAccesses = null,
    IReadOnlyList<ResourceGrant>? ContainerAccesses = null,
    IReadOnlyList<ResourceGrant>? WebsiteAccesses = null,
    IReadOnlyList<ResourceGrant>? SearchEngineAccesses = null,
    IReadOnlyList<ResourceGrant>? LocalInfoStoreAccesses = null,
    IReadOnlyList<ResourceGrant>? ExternalInfoStoreAccesses = null,
    IReadOnlyList<ResourceGrant>? AudioDeviceAccesses = null,
    IReadOnlyList<ResourceGrant>? AgentAccesses = null,
    IReadOnlyList<ResourceGrant>? TaskAccesses = null,
    IReadOnlyList<ResourceGrant>? SkillAccesses = null);

/// <summary>
/// A single per-resource grant entry. <see cref="ResourceId"/> is the
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

public sealed record RolePermissionsResponse(
    Guid RoleId,
    string RoleName,
    PermissionClearance DefaultClearance,

    bool CanCreateSubAgents,
    bool CanCreateContainers,
    bool CanRegisterInfoStores,
    bool CanAccessLocalhostInBrowser,
    bool CanAccessLocalhostCli,

    IReadOnlyList<ResourceGrant> DangerousShellAccesses,
    IReadOnlyList<ResourceGrant> SafeShellAccesses,
    IReadOnlyList<ResourceGrant> ContainerAccesses,
    IReadOnlyList<ResourceGrant> WebsiteAccesses,
    IReadOnlyList<ResourceGrant> SearchEngineAccesses,
    IReadOnlyList<ResourceGrant> LocalInfoStoreAccesses,
    IReadOnlyList<ResourceGrant> ExternalInfoStoreAccesses,
    IReadOnlyList<ResourceGrant> AudioDeviceAccesses,
    IReadOnlyList<ResourceGrant> AgentAccesses,
    IReadOnlyList<ResourceGrant> TaskAccesses,
    IReadOnlyList<ResourceGrant> SkillAccesses);
