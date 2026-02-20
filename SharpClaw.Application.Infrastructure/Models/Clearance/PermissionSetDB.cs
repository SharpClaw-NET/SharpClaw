using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Clearance;

/// <summary>
/// A reusable set of permissions that can be attached to a role, context,
/// or conversation. Defines what actions the holder is permitted to
/// perform and at what clearance level.
/// </summary>
public class PermissionSetDB : BaseEntity
{
    // ── Default clearance ─────────────────────────────────────────

    /// <summary>
    /// Fallback clearance level used when an individual permission's
    /// <see cref="PermissionClearance"/> is <see cref="PermissionClearance.Unset"/>.
    /// </summary>
    public PermissionClearance DefaultClearance { get; set; } = PermissionClearance.Unset;

    // ── Global flags ──────────────────────────────────────────────

    /// <summary>Execute shell commands as administrator / sudo.</summary>
    public bool CanExecuteAsAdmin { get; set; }

    /// <summary>Create sub-agents (must have ≤ the creator's permissions).</summary>
    public bool CanCreateSubAgents { get; set; }

    /// <summary>Create new sandbox / VM / container environments.</summary>
    public bool CanCreateContainers { get; set; }

    /// <summary>Register new local or external information stores.</summary>
    public bool CanRegisterInfoStores { get; set; }

    /// <summary>Edit any scheduled task (when false, per-task grants apply).</summary>
    public bool CanEditAllTasks { get; set; }

    // ── Per-resource grant collections ────────────────────────────

    public ICollection<SystemUserAccessDB> SystemUserAccesses { get; set; } = [];
    public ICollection<LocalInfoStoreAccessDB> LocalInfoStorePermissions { get; set; } = [];
    public ICollection<ExternalInfoStoreAccessDB> ExternalInfoStorePermissions { get; set; } = [];
    public ICollection<WebsiteAccessDB> WebsiteAccesses { get; set; } = [];
    public ICollection<SearchEngineAccessDB> SearchEngineAccesses { get; set; } = [];
    public ICollection<ContainerAccessDB> ContainerAccesses { get; set; } = [];
    public ICollection<AudioDeviceAccessDB> AudioDeviceAccesses { get; set; } = [];
    public ICollection<AgentManagementAccessDB> AgentPermissions { get; set; } = [];
    public ICollection<TaskManageAccessDB> TaskPermissions { get; set; } = [];
    public ICollection<SkillManageAccessDB> SkillPermissions { get; set; } = [];

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
