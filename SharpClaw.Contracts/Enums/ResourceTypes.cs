namespace SharpClaw.Contracts.Enums;

/// <summary>
/// String constants that identify each per-resource permission category.
/// Used as the <see cref="Infrastructure.Models.Access.ResourceAccessDB.ResourceType"/>
/// discriminator in the unified resource access table.
/// <para>
/// Names follow the <c>{ToolPrefix}{ResourceKind}</c> convention, binding each
/// resource type to its owning module (see Module-System-Design §3.10.3).
/// Exceptions: <see cref="Container"/> (host-managed, no prefix) and
/// <see cref="EditorSession"/> (shared between vs/vsc modules).
/// </para>
/// </summary>
public static class ResourceTypes
{
    /// <summary>Dangerous Shell module — unrestricted OS shell execution targeting a system user.</summary>
    public const string DsShell = "DsShell";

    /// <summary>mk8.shell module — sandboxed mk8.shell execution targeting a container.</summary>
    public const string Mk8Shell = "Mk8Shell";

    /// <summary>Host-managed OS-level sandbox container (no module prefix).</summary>
    public const string Container = "Container";

    /// <summary>Web Access module — HTTP/website access.</summary>
    public const string WaWebsite = "WaWebsite";

    /// <summary>Web Access module — search engine query access.</summary>
    public const string WaSearch = "WaSearch";

    /// <summary>Database Access module — internal (local) database access.</summary>
    public const string DbInternal = "DbInternal";

    /// <summary>Database Access module — external (remote) database access.</summary>
    public const string DbExternal = "DbExternal";

    /// <summary>Transcription module — audio input device access.</summary>
    public const string TrAudio = "TrAudio";

    /// <summary>Computer Use module — display device capture access.</summary>
    public const string CuDisplay = "CuDisplay";

    /// <summary>Shared (vs/vsc) — IDE editor session access (no single-module prefix).</summary>
    public const string EditorSession = "EditorSession";

    /// <summary>Agent Orchestration module — agent management access.</summary>
    public const string AoAgent = "AoAgent";

    /// <summary>Agent Orchestration module — task editing access.</summary>
    public const string AoTask = "AoTask";

    /// <summary>Agent Orchestration module — skill access.</summary>
    public const string AoSkill = "AoSkill";

    /// <summary>Agent Orchestration module — agent header editing access.</summary>
    public const string AoAgentHeader = "AoAgentHeader";

    /// <summary>Agent Orchestration module — channel header editing access.</summary>
    public const string AoChannelHeader = "AoChannelHeader";

    /// <summary>Bot Integration module — bot platform channel access.</summary>
    public const string BiChannel = "BiChannel";

    /// <summary>Office Apps module — document session access.</summary>
    public const string OaDocument = "OaDocument";

    /// <summary>Computer Use module — native application launch/stop access.</summary>
    public const string CuNativeApp = "CuNativeApp";

    /// <summary>All resource type constants (for iteration).</summary>
    public static readonly string[] AllTypes =
    [
        DsShell, Mk8Shell, Container, WaWebsite, WaSearch,
        DbInternal, DbExternal, TrAudio, CuDisplay, EditorSession,
        AoAgent, AoTask, AoSkill, AoAgentHeader, AoChannelHeader,
        BiChannel, OaDocument, CuNativeApp
    ];

    /// <summary>
    /// Maps <c>DelegateTo</c> method names (used by modules) to the
    /// corresponding <see cref="ResourceTypes"/> constant.
    /// Global-flag delegates map to <c>null</c> (no resource type).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> ByDelegateName =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Global flags — no resource type
            ["CreateSubAgentAsync"] = null,
            ["CreateContainerAsync"] = null,
            ["RegisterDatabaseAsync"] = null,
            ["AccessLocalhostInBrowserAsync"] = null,
            ["AccessLocalhostCliAsync"] = null,
            ["ClickDesktopAsync"] = null,
            ["TypeOnDesktopAsync"] = null,
            ["ReadCrossThreadHistoryAsync"] = null,
            ["CreateDocumentSessionAsync"] = null,
            ["EnumerateWindowsAsync"] = null,
            ["FocusWindowAsync"] = null,
            ["CloseWindowAsync"] = null,
            ["ResizeWindowAsync"] = null,
            ["SendHotkeyAsync"] = null,
            ["ReadClipboardAsync"] = null,
            ["WriteClipboardAsync"] = null,

            // Per-resource — keyed by DelegateTo method name
            ["UnsafeExecuteAsDangerousShellAsync"] = DsShell,
            ["ExecuteAsSafeShellAsync"] = Mk8Shell,
            ["AccessInternalDatabaseAsync"] = DbInternal,
            ["AccessExternalDatabaseAsync"] = DbExternal,
            ["AccessWebsiteAsync"] = WaWebsite,
            ["QuerySearchEngineAsync"] = WaSearch,
            ["AccessContainerAsync"] = Container,
            ["ManageAgentAsync"] = AoAgent,
            ["EditTaskAsync"] = AoTask,
            ["AccessSkillAsync"] = AoSkill,
            ["AccessInputAudioAsync"] = TrAudio,
            ["AccessDisplayDeviceAsync"] = CuDisplay,
            ["AccessEditorSessionAsync"] = EditorSession,
            ["AccessBotIntegrationAsync"] = BiChannel,
            ["AccessDocumentSessionAsync"] = OaDocument,
            ["LaunchNativeApplicationAsync"] = CuNativeApp,
            ["EditAgentHeaderAsync"] = AoAgentHeader,
            ["EditChannelHeaderAsync"] = AoChannelHeader,
        };
}
