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
    EditorRunTerminal = 30
}
