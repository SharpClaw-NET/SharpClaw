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

    /// <summary>Register new internal or external databases.</summary>
    public bool CanRegisterDatabases { get; set; }
    public PermissionClearance RegisterDatabasesClearance { get; set; } = PermissionClearance.Unset;

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

    /// <summary>
    /// Allow agent to register new document sessions (via register_document tool
    /// or spreadsheet_create_workbook auto-registration).
    /// When false, agent can only use pre-registered documents.
    /// </summary>
    public bool CanCreateDocumentSessions { get; set; }
    public PermissionClearance CreateDocumentSessionsClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Enumerate visible desktop windows (title, process, handle).</summary>
    public bool CanEnumerateWindows { get; set; }
    public PermissionClearance EnumerateWindowsClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Bring a window to the foreground.</summary>
    public bool CanFocusWindow { get; set; }
    public PermissionClearance FocusWindowClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Send WM_CLOSE to a window (graceful close).</summary>
    public bool CanCloseWindow { get; set; }
    public PermissionClearance CloseWindowClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Move, resize, minimize, or maximize a window.</summary>
    public bool CanResizeWindow { get; set; }
    public PermissionClearance ResizeWindowClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Send keyboard shortcuts (Ctrl+S, Alt+Tab, etc.).</summary>
    public bool CanSendHotkey { get; set; }
    public PermissionClearance SendHotkeyClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Read clipboard contents (text, files, images).</summary>
    public bool CanReadClipboard { get; set; }
    public PermissionClearance ReadClipboardClearance { get; set; } = PermissionClearance.Unset;

    /// <summary>Set clipboard contents (text or file paths).</summary>
    public bool CanWriteClipboard { get; set; }
    public PermissionClearance WriteClipboardClearance { get; set; } = PermissionClearance.Unset;

    // ── Per-resource grant collections ────────────────────

    public ICollection<DangerousShellAccessDB> DangerousShellAccesses { get; set; } = [];
    public ICollection<SafeShellAccessDB> SafeShellAccesses { get; set; } = [];
    public ICollection<InternalDatabaseAccessDB> InternalDatabaseAccesses { get; set; } = [];
    public ICollection<ExternalDatabaseAccessDB> ExternalDatabaseAccesses { get; set; } = [];
    public ICollection<WebsiteAccessDB> WebsiteAccesses { get; set; } = [];
    public ICollection<SearchEngineAccessDB> SearchEngineAccesses { get; set; } = [];
    public ICollection<ContainerAccessDB> ContainerAccesses { get; set; } = [];
    public ICollection<InputAudioAccessDB> InputAudioAccesses { get; set; } = [];
    public ICollection<DisplayDeviceAccessDB> DisplayDeviceAccesses { get; set; } = [];
    public ICollection<EditorSessionAccessDB> EditorSessionAccesses { get; set; } = [];
    public ICollection<AgentManagementAccessDB> AgentPermissions { get; set; } = [];
    public ICollection<TaskManageAccessDB> TaskPermissions { get; set; } = [];
    public ICollection<SkillManageAccessDB> SkillPermissions { get; set; } = [];
    public ICollection<AgentHeaderAccessDB> AgentHeaderAccesses { get; set; } = [];
    public ICollection<ChannelHeaderAccessDB> ChannelHeaderAccesses { get; set; } = [];
    public ICollection<BotIntegrationAccessDB> BotIntegrationAccesses { get; set; } = [];
    public ICollection<DocumentSessionAccessDB> DocumentSessionAccesses { get; set; } = [];
    public ICollection<NativeApplicationAccessDB> NativeApplicationAccesses { get; set; } = [];

    // ── Default resource accesses ─────────────────────────────────
    // Optional defaults used when starting a job and no specific
    // resource of that type is provided by the context or channel.

    public Guid? DefaultDangerousShellAccessId { get; set; }
    public DangerousShellAccessDB? DefaultDangerousShellAccess { get; set; }

    public Guid? DefaultSafeShellAccessId { get; set; }
    public SafeShellAccessDB? DefaultSafeShellAccess { get; set; }

    public Guid? DefaultInternalDatabaseAccessId { get; set; }
    public InternalDatabaseAccessDB? DefaultInternalDatabaseAccess { get; set; }

    public Guid? DefaultExternalDatabaseAccessId { get; set; }
    public ExternalDatabaseAccessDB? DefaultExternalDatabaseAccess { get; set; }

    public Guid? DefaultWebsiteAccessId { get; set; }
    public WebsiteAccessDB? DefaultWebsiteAccess { get; set; }

    public Guid? DefaultSearchEngineAccessId { get; set; }
    public SearchEngineAccessDB? DefaultSearchEngineAccess { get; set; }

    public Guid? DefaultContainerAccessId { get; set; }
    public ContainerAccessDB? DefaultContainerAccess { get; set; }

    public Guid? DefaultInputAudioAccessId { get; set; }
    public InputAudioAccessDB? DefaultInputAudioAccess { get; set; }

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

    public Guid? DefaultBotIntegrationAccessId { get; set; }
    public BotIntegrationAccessDB? DefaultBotIntegrationAccess { get; set; }

    public Guid? DefaultDocumentSessionAccessId { get; set; }
    public DocumentSessionAccessDB? DefaultDocumentSessionAccess { get; set; }

    public Guid? DefaultNativeApplicationAccessId { get; set; }
    public NativeApplicationAccessDB? DefaultNativeApplicationAccess { get; set; }

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
