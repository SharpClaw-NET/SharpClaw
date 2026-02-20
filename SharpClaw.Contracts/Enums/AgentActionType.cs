namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Identifies which <see cref="AgentActionService"/> method a job dispatches to.
/// </summary>
public enum AgentActionType
{
    // ── Global flags ──────────────────────────────────────────────
    ExecuteAsAdmin,
    CreateSubAgent,
    CreateContainer,
    RegisterInfoStore,
    EditAnyTask,

    // ── Per-resource grants ───────────────────────────────────────
    ExecuteAsSystemUser,
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
