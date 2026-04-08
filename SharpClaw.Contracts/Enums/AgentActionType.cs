namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Identifies which <see cref="AgentActionService"/> method a job dispatches to.
/// </summary>
public enum AgentActionType
{
    // ── Global flags ──────────────────────────────────────────────
    CreateSubAgent = 0,
    [Obsolete("Dispatched to mk8.shell module.")]
    CreateContainer = 1,
    [Obsolete("Dispatched to Database Access module.")]
    RegisterDatabase = 2,
    AccessLocalhostInBrowser = 3,
    AccessLocalhostCli = 4,
    [Obsolete("Dispatched to Computer Use module.")]
    ClickDesktop = 5,
    [Obsolete("Dispatched to Computer Use module.")]
    TypeOnDesktop = 6,

    // ── Per-resource grants ───────────────────────────────────────
    [Obsolete("Dispatched to Dangerous Shell module.")]
    UnsafeExecuteAsDangerousShell = 7,
    [Obsolete("Dispatched to mk8.shell module.")]
    ExecuteAsSafeShell = 8,
    [Obsolete("Dispatched to Database Access module.")]
    AccessInternalDatabases = 9,
    [Obsolete("Dispatched to Database Access module.")]
    AccessExternalDatabase = 10,
    AccessWebsite = 11,
    QuerySearchEngine = 12,
    AccessContainer = 13,
    ManageAgent = 14,
    EditTask = 15,
    AccessSkill = 16,

    // ── Transcription actions (per-resource: audio device) ────────
    TranscribeFromAudioDevice = 17,
    TranscribeFromAudioStream = 18,
    TranscribeFromAudioFile = 19,

    // ── Display capture (per-resource: display device) ────────────
    [Obsolete("Dispatched to Computer Use module.")]
    CaptureDisplay = 20,

    // ── Editor actions (per-resource: editor session) ─────────────
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorReadFile = 21,
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorGetOpenFiles = 22,
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorGetSelection = 23,
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorGetDiagnostics = 24,
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorApplyEdit = 25,
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorCreateFile = 26,
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorDeleteFile = 27,
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorShowDiff = 28,
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorRunBuild = 29,
    [Obsolete("Dispatched to VS 2026 Editor / VS Code Editor module.")]
    EditorRunTerminal = 30,

    // ── Cross-thread context access (global flag) ─────────────────
    ReadCrossThreadHistory = 31,

    // ── Header editing (per-resource: target agent / channel) ──────
    EditAgentHeader = 32,
    EditChannelHeader = 33,

    // ── Bot messaging (per-resource: bot integration) ──────────────
    SendBotMessage = 34,

    // ── Document session management (global flag) ─────────────────
    [Obsolete("Dispatched to Office Apps module.")]
    CreateDocumentSession = 35,

    // ── File-based spreadsheet actions (per-resource: document session)
    // Uses ClosedXML for .xlsx/.xlsm, CsvHelper for .csv.
    // Operates on the file directly — fails if file is locked.
    [Obsolete("Dispatched to Office Apps module.")]
    SpreadsheetReadRange = 36,
    [Obsolete("Dispatched to Office Apps module.")]
    SpreadsheetWriteRange = 37,
    [Obsolete("Dispatched to Office Apps module.")]
    SpreadsheetListSheets = 38,
    [Obsolete("Dispatched to Office Apps module.")]
    SpreadsheetCreateSheet = 39,
    [Obsolete("Dispatched to Office Apps module.")]
    SpreadsheetDeleteSheet = 40,
    [Obsolete("Dispatched to Office Apps module.")]
    SpreadsheetGetInfo = 41,
    [Obsolete("Dispatched to Office Apps module.")]
    SpreadsheetCreateWorkbook = 42,

    // ── Live spreadsheet actions via COM Interop (per-resource: document session)
    // Operates on the running Excel instance — Windows only.
    // Agent explicitly chooses this when the file is open in Excel.
    [Obsolete("Dispatched to Office Apps module.")]
    SpreadsheetLiveReadRange = 43,
    [Obsolete("Dispatched to Office Apps module.")]
    SpreadsheetLiveWriteRange = 44,

    // ── Desktop awareness (global flags + per-resource) ───────────
    [Obsolete("Dispatched to Computer Use module.")]
    EnumerateWindows = 45,
    [Obsolete("Dispatched to Computer Use module.")]
    LaunchNativeApplication = 46,

    // ── Window management (global flags) ─────────────────────────
    [Obsolete("Dispatched to Computer Use module.")]
    FocusWindow = 47,
    [Obsolete("Dispatched to Computer Use module.")]
    CloseWindow = 48,
    [Obsolete("Dispatched to Computer Use module.")]
    ResizeWindow = 49,

    // ── Hotkey (global flag) ─────────────────────────────────────
    [Obsolete("Dispatched to Computer Use module.")]
    SendHotkey = 50,

    // ── Window capture (per-resource: display device) ────────────
    [Obsolete("Dispatched to Computer Use module.")]
    CaptureWindow = 51,

    // ── Clipboard (global flags) ─────────────────────────────────
    [Obsolete("Dispatched to Computer Use module.")]
    ReadClipboard = 52,
    [Obsolete("Dispatched to Computer Use module.")]
    WriteClipboard = 53,

    // ── Process control (per-resource: native application) ───────
    [Obsolete("Dispatched to Computer Use module.")]
    StopProcess = 54,

    // ── Module system ─────────────────────────────────────────────
    // Values 55–99 are reserved for future built-in actions.

    /// <summary>
    /// Category tag for module-provided tool calls.
    /// The specific tool is identified by <c>AgentJobDB.ActionKey</c>,
    /// which holds the prefixed tool name (e.g. "cu_enumerate_windows").
    /// <c>ScriptJson</c> carries the module envelope for parameter extraction:
    /// <c>{ "module": "computer_use", "tool": "enumerate_windows", "params": { ... } }</c>
    /// <para>
    /// <b>Note:</b> Callers may also use the original <see cref="AgentActionType"/>
    /// enum value (e.g. <see cref="ClickDesktop"/>). The dispatch system automatically
    /// derives a snake_case tool name from the enum name and resolves it through the
    /// module registry, so explicit use of <c>ModuleAction</c> is not required for
    /// migrated actions.
    /// </para>
    /// </summary>
    ModuleAction = 100,
}
