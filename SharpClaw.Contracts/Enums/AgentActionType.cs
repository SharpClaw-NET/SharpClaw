namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Identifies which <see cref="AgentActionService"/> method a job dispatches to.
/// </summary>
public enum AgentActionType
{
    // ── Global flags ──────────────────────────────────────────────
    CreateSubAgent,
    CreateContainer,
    RegisterInfoStore,
    AccessLocalhostInBrowser,
    AccessLocalhostCli,
    ClickDesktop,
    TypeOnDesktop,

    // ── Per-resource grants ───────────────────────────────────────
    UnsafeExecuteAsDangerousShell,
    ExecuteAsSafeShell,
    AccessLocalInfoStore,
    AccessExternalInfoStore,
    AccessWebsite,
    QuerySearchEngine,
    AccessContainer,
    ManageAgent,
    EditTask,
    AccessSkill,

    // ── Transcription actions (per-resource: audio device) ────────
    TranscribeFromAudioDevice,
    TranscribeFromAudioStream,
    TranscribeFromAudioFile,

    // ── Display capture (per-resource: display device) ────────────
    CaptureDisplay,

    // ── Editor actions (per-resource: editor session) ─────────────
    EditorReadFile,
    EditorGetOpenFiles,
    EditorGetSelection,
    EditorGetDiagnostics,
    EditorApplyEdit,
    EditorCreateFile,
    EditorDeleteFile,
    EditorShowDiff,
    EditorRunBuild,
    EditorRunTerminal
}
