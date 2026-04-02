namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Identifies which <see cref="AgentActionService"/> method a job dispatches to.
/// </summary>
public enum AgentActionType
{
    // ── Global flags ──────────────────────────────────────────────
    CreateSubAgent = 0,
    CreateContainer = 1,
    RegisterInfoStore = 2,
    AccessLocalhostInBrowser = 3,
    AccessLocalhostCli = 4,
    ClickDesktop = 5,
    TypeOnDesktop = 6,

    // ── Per-resource grants ───────────────────────────────────────
    UnsafeExecuteAsDangerousShell = 7,
    ExecuteAsSafeShell = 8,
    AccessLocalInfoStore = 9,
    AccessExternalInfoStore = 10,
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
    CaptureDisplay = 20,

    // ── Editor actions (per-resource: editor session) ─────────────
    EditorReadFile = 21,
    EditorGetOpenFiles = 22,
    EditorGetSelection = 23,
    EditorGetDiagnostics = 24,
    EditorApplyEdit = 25,
    EditorCreateFile = 26,
    EditorDeleteFile = 27,
    EditorShowDiff = 28,
    EditorRunBuild = 29,
    EditorRunTerminal = 30,

    // ── Cross-thread context access (global flag) ─────────────────
    ReadCrossThreadHistory = 31,

    // ── Header editing (per-resource: target agent / channel) ──────
    EditAgentHeader = 32,
    EditChannelHeader = 33,

    // ── Bot messaging (per-resource: bot integration) ──────────────
    SendBotMessage = 34,

    // ── Document session management (global flag) ─────────────────
    CreateDocumentSession = 35,

    // ── File-based spreadsheet actions (per-resource: document session)
    // Uses ClosedXML for .xlsx/.xlsm, CsvHelper for .csv.
    // Operates on the file directly — fails if file is locked.
    SpreadsheetReadRange = 36,
    SpreadsheetWriteRange = 37,
    SpreadsheetListSheets = 38,
    SpreadsheetCreateSheet = 39,
    SpreadsheetDeleteSheet = 40,
    SpreadsheetGetInfo = 41,
    SpreadsheetCreateWorkbook = 42,

    // ── Live spreadsheet actions via COM Interop (per-resource: document session)
    // Operates on the running Excel instance — Windows only.
    // Agent explicitly chooses this when the file is open in Excel.
    SpreadsheetLiveReadRange = 43,
    SpreadsheetLiveWriteRange = 44,

    // ── Desktop awareness (global flags + per-resource) ───────────
    EnumerateWindows = 45,
    LaunchNativeApplication = 46,

    // ── Window management (global flags) ─────────────────────────
    FocusWindow = 47,
    CloseWindow = 48,
    ResizeWindow = 49,

    // ── Hotkey (global flag) ─────────────────────────────────────
    SendHotkey = 50,

    // ── Window capture (per-resource: display device) ────────────
    CaptureWindow = 51,

    // ── Clipboard (global flags) ─────────────────────────────────
    ReadClipboard = 52,
    WriteClipboard = 53,

    // ── Process control (per-resource: native application) ───────
    StopProcess = 54,
}
