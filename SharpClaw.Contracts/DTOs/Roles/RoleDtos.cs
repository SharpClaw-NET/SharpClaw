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
/// </summary>
public sealed record SetRolePermissionsRequest(
    PermissionClearance DefaultClearance = PermissionClearance.Unset,

    // Global flags
    bool CanCreateSubAgents = false,
    PermissionClearance CreateSubAgentsClearance = PermissionClearance.Unset,
    bool CanCreateContainers = false,
    PermissionClearance CreateContainersClearance = PermissionClearance.Unset,
    bool CanRegisterInfoStores = false,
    PermissionClearance RegisterInfoStoresClearance = PermissionClearance.Unset,
    bool CanAccessLocalhostInBrowser = false,
    PermissionClearance AccessLocalhostInBrowserClearance = PermissionClearance.Unset,
    bool CanAccessLocalhostCli = false,
    PermissionClearance AccessLocalhostCliClearance = PermissionClearance.Unset,
    bool CanClickDesktop = false,
    PermissionClearance ClickDesktopClearance = PermissionClearance.Unset,
    bool CanTypeOnDesktop = false,
    PermissionClearance TypeOnDesktopClearance = PermissionClearance.Unset,
    bool CanReadCrossThreadHistory = false,
    PermissionClearance ReadCrossThreadHistoryClearance = PermissionClearance.Unset,
    bool CanEditAgentHeader = false,
    PermissionClearance EditAgentHeaderClearance = PermissionClearance.Unset,
    bool CanEditChannelHeader = false,
    PermissionClearance EditChannelHeaderClearance = PermissionClearance.Unset,

    // Per-resource grants
    IReadOnlyList<ResourceGrant>? DangerousShellAccesses = null,
    IReadOnlyList<ResourceGrant>? SafeShellAccesses = null,
    IReadOnlyList<ResourceGrant>? ContainerAccesses = null,
    IReadOnlyList<ResourceGrant>? WebsiteAccesses = null,
    IReadOnlyList<ResourceGrant>? SearchEngineAccesses = null,
    IReadOnlyList<ResourceGrant>? LocalInfoStoreAccesses = null,
    IReadOnlyList<ResourceGrant>? ExternalInfoStoreAccesses = null,
    IReadOnlyList<ResourceGrant>? AudioDeviceAccesses = null,
    IReadOnlyList<ResourceGrant>? DisplayDeviceAccesses = null,
    IReadOnlyList<ResourceGrant>? EditorSessionAccesses = null,
    IReadOnlyList<ResourceGrant>? AgentAccesses = null,
    IReadOnlyList<ResourceGrant>? TaskAccesses = null,
    IReadOnlyList<ResourceGrant>? SkillAccesses = null,
    IReadOnlyList<ResourceGrant>? AgentHeaderAccesses = null,
    IReadOnlyList<ResourceGrant>? ChannelHeaderAccesses = null);

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

public sealed record RolePermissionsResponse(
    Guid RoleId,
    string RoleName,
    PermissionClearance DefaultClearance,

    bool CanCreateSubAgents,
    PermissionClearance CreateSubAgentsClearance,
    bool CanCreateContainers,
    PermissionClearance CreateContainersClearance,
    bool CanRegisterInfoStores,
    PermissionClearance RegisterInfoStoresClearance,
    bool CanAccessLocalhostInBrowser,
    PermissionClearance AccessLocalhostInBrowserClearance,
    bool CanAccessLocalhostCli,
    PermissionClearance AccessLocalhostCliClearance,
    bool CanClickDesktop,
    PermissionClearance ClickDesktopClearance,
    bool CanTypeOnDesktop,
    PermissionClearance TypeOnDesktopClearance,
    bool CanReadCrossThreadHistory,
    PermissionClearance ReadCrossThreadHistoryClearance,
    bool CanEditAgentHeader,
    PermissionClearance EditAgentHeaderClearance,
    bool CanEditChannelHeader,
    PermissionClearance EditChannelHeaderClearance,

    IReadOnlyList<ResourceGrant> DangerousShellAccesses,
    IReadOnlyList<ResourceGrant> SafeShellAccesses,
    IReadOnlyList<ResourceGrant> ContainerAccesses,
    IReadOnlyList<ResourceGrant> WebsiteAccesses,
    IReadOnlyList<ResourceGrant> SearchEngineAccesses,
    IReadOnlyList<ResourceGrant> LocalInfoStoreAccesses,
    IReadOnlyList<ResourceGrant> ExternalInfoStoreAccesses,
    IReadOnlyList<ResourceGrant> AudioDeviceAccesses,
    IReadOnlyList<ResourceGrant> DisplayDeviceAccesses,
    IReadOnlyList<ResourceGrant> EditorSessionAccesses,
    IReadOnlyList<ResourceGrant> AgentAccesses,
    IReadOnlyList<ResourceGrant> TaskAccesses,
    IReadOnlyList<ResourceGrant> SkillAccesses,
    IReadOnlyList<ResourceGrant> AgentHeaderAccesses,
    IReadOnlyList<ResourceGrant> ChannelHeaderAccesses);
