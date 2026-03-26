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
    // ── Default clearance ─────────────────────────────────────────

    /// <summary>
    /// Fallback clearance level used when an individual permission's
    /// <see cref="PermissionClearance"/> is <see cref="PermissionClearance.Unset"/>.
    /// </summary>
    public PermissionClearance DefaultClearance { get; set; } = PermissionClearance.Unset;

    // ── Global flags ──────────────────────────────────────────────

    /// <summary>Create sub-agents (must have = the creator's permissions).</summary>
    public bool CanCreateSubAgents { get; set; }
    public PermissionClearance CreateSubAgentsClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Create new sandbox / VM / container environments.</summary>
    public bool CanCreateContainers { get; set; }
    public PermissionClearance CreateContainersClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Register new local or external information stores.</summary>
    public bool CanRegisterInfoStores { get; set; }
    public PermissionClearance RegisterInfoStoresClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Access localhost URLs through a browser (headless Chrome by default).</summary>
    public bool CanAccessLocalhostInBrowser { get; set; }
    public PermissionClearance AccessLocalhostInBrowserClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Access localhost URLs via direct HTTP (no browser).</summary>
    public bool CanAccessLocalhostCli { get; set; }
    public PermissionClearance AccessLocalhostCliClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Simulate mouse clicks on desktop displays.</summary>
    public bool CanClickDesktop { get; set; }
    public PermissionClearance ClickDesktopClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Simulate keyboard input on desktop displays.</summary>
    public bool CanTypeOnDesktop { get; set; }
    public PermissionClearance TypeOnDesktopClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>
    /// Read conversation history from other threads/channels where this
    /// agent is allowed. The target channel must also opt-in by having
    /// this flag set in its own (or its context's) permission set.
    /// </summary>
    public bool CanReadCrossThreadHistory { get; set; }
    public PermissionClearance ReadCrossThreadHistoryClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Edit the custom chat header of specific agents.</summary>
    public bool CanEditAgentHeader { get; set; }
    public PermissionClearance EditAgentHeaderClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Edit the custom chat header of specific channels.</summary>
    public bool CanEditChannelHeader { get; set; }
    public PermissionClearance EditChannelHeaderClearance { get; set; } = PermissionClearance.Unset;

    // ── Per-resource grant collections ────────────────────

    public ICollection<DangerousShellAccessDB> DangerousShellAccesses { get; set; } = [];
    public ICollection<SafeShellAccessDB> SafeShellAccesses { get; set; } = [];
    public ICollection<LocalInfoStoreAccessDB> LocalInfoStorePermissions { get; set; } = [];
    public ICollection<ExternalInfoStoreAccessDB> ExternalInfoStorePermissions { get; set; } = [];
    public ICollection<WebsiteAccessDB> WebsiteAccesses { get; set; } = [];
    public ICollection<SearchEngineAccessDB> SearchEngineAccesses { get; set; } = [];
    public ICollection<ContainerAccessDB> ContainerAccesses { get; set; } = [];
    public ICollection<AudioDeviceAccessDB> AudioDeviceAccesses { get; set; } = [];
    public ICollection<DisplayDeviceAccessDB> DisplayDeviceAccesses { get; set; } = [];
    public ICollection<EditorSessionAccessDB> EditorSessionAccesses { get; set; } = [];
    public ICollection<AgentManagementAccessDB> AgentPermissions { get; set; } = [];
    public ICollection<TaskManageAccessDB> TaskPermissions { get; set; } = [];
    public ICollection<SkillManageAccessDB> SkillPermissions { get; set; } = [];
    public ICollection<AgentHeaderAccessDB> AgentHeaderAccesses { get; set; } = [];
    public ICollection<ChannelHeaderAccessDB> ChannelHeaderAccesses { get; set; } = [];

    // ── Default resource accesses ─────────────────────────────────
    // Optional defaults used when starting a job and no specific
    // resource of that type is provided by the context or channel.

    public Guid? DefaultDangerousShellAccessId { get; set; }
    public DangerousShellAccessDB? DefaultDangerousShellAccess { get; set; }

    public Guid? DefaultSafeShellAccessId { get; set; }
    public SafeShellAccessDB? DefaultSafeShellAccess { get; set; }

    public Guid? DefaultLocalInfoStorePermissionId { get; set; }
    public LocalInfoStoreAccessDB? DefaultLocalInfoStorePermission { get; set; }

    public Guid? DefaultExternalInfoStorePermissionId { get; set; }
    public ExternalInfoStoreAccessDB? DefaultExternalInfoStorePermission { get; set; }

    public Guid? DefaultWebsiteAccessId { get; set; }
    public WebsiteAccessDB? DefaultWebsiteAccess { get; set; }

    public Guid? DefaultSearchEngineAccessId { get; set; }
    public SearchEngineAccessDB? DefaultSearchEngineAccess { get; set; }

    public Guid? DefaultContainerAccessId { get; set; }
    public ContainerAccessDB? DefaultContainerAccess { get; set; }

    public Guid? DefaultAudioDeviceAccessId { get; set; }
    public AudioDeviceAccessDB? DefaultAudioDeviceAccess { get; set; }

    public Guid? DefaultDisplayDeviceAccessId { get; set; }
    public DisplayDeviceAccessDB? DefaultDisplayDeviceAccess { get; set; }

    public Guid? DefaultEditorSessionAccessId { get; set; }
    public EditorSessionAccessDB? DefaultEditorSessionAccess { get; set; }

    public Guid? DefaultAgentPermissionId { get; set; }
    public AgentManagementAccessDB? DefaultAgentPermission { get; set; }

    public Guid? DefaultTaskPermissionId { get; set; }
    public TaskManageAccessDB? DefaultTaskPermission { get; set; }

    public Guid? DefaultSkillPermissionId { get; set; }
    public SkillManageAccessDB? DefaultSkillPermission { get; set; }

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
