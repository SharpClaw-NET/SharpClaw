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
    TranscribeFromAudioFile
}
