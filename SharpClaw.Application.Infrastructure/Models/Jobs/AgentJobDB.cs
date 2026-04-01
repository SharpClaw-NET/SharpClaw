using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;

namespace SharpClaw.Application.Infrastructure.Models.Jobs;

/// <summary>
/// Persisted record of an agent action job.  Tracks the full lifecycle
/// from submission through permission evaluation, optional approval,
/// execution, and final outcome.
/// </summary>
public class AgentJobDB : BaseEntity
{
    public Guid AgentId { get; set; }
    public AgentDB Agent { get; set; } = null!;

    // ── Caller identity (who submitted the job) ───────────────────
    public Guid? CallerUserId { get; set; }
    public Guid? CallerAgentId { get; set; }

    // ── Action definition ─────────────────────────────────────────
    public AgentActionType ActionType { get; set; }
    public Guid? ResourceId { get; set; }

    // ── Shell type (for shell execution actions) ──────────────────
    //
    //  Safe (ExecuteAsSafeShell)     → mk8.shell only (sandboxed DSL).
    //  Dangerous (UnsafeExecuteAs…)  → real Bash/PowerShell/Cmd/Git.
    //
    //  mk8.shell is ALWAYS safe.  Bash/PowerShell/Cmd/Git are ALWAYS
    //  dangerous.  There is no crossover.
    //
    public DangerousShellType? DangerousShellType { get; set; }
    public SafeShellType? SafeShellType { get; set; }

    /// <summary>
    /// Payload submitted with shell jobs.  The format depends on the
    /// action type:
    /// <list type="bullet">
    ///   <item><b>Safe shell</b> (<see cref="AgentActionType.ExecuteAsSafeShell"/>):
    ///     serialised <see cref="Mk8.Shell.Engine.Mk8ShellScript"/> JSON.</item>
    ///   <item><b>Dangerous shell</b> (<see cref="AgentActionType.UnsafeExecuteAsDangerousShell"/>):
    ///     raw command text passed directly to the real shell interpreter.</item>
    /// </list>
    /// </summary>
    public string? ScriptJson { get; set; }

    /// <summary>
    /// Absolute path where the dangerous shell process should be
    /// spawned.  Overrides the <see cref="SystemUserDB.WorkingDirectory"/>
    /// when set.  Not validated or sandboxed — dangerous by design.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    // ── State ─────────────────────────────────────────────────────
    public AgentJobStatus Status { get; set; } = AgentJobStatus.Queued;
    public PermissionClearance EffectiveClearance { get; set; } = PermissionClearance.Unset;

    // ── Outcome ───────────────────────────────────────────────────
    public string? ResultData { get; set; }
    public string? ErrorLog { get; set; }

    // ── Timestamps ────────────────────────────────────────────────
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // ── Approval ──────────────────────────────────────────────────
    public Guid? ApprovedByUserId { get; set; }
    public Guid? ApprovedByAgentId { get; set; }

    // ── Transcription ───────────────────────────────────────────
    public Guid? TranscriptionModelId { get; set; }
    public ModelDB? TranscriptionModel { get; set; }

    /// <summary>
    /// Pipeline mode: <see cref="Enums.TranscriptionMode.SlidingWindow"/>
    /// (overlapping, two-pass) or <see cref="Enums.TranscriptionMode.StrictWindow"/>
    /// (sequential full windows, final-only).  Null = default (SlidingWindow).
    /// </summary>
    public TranscriptionMode? TranscriptionMode { get; set; }

    /// <summary>
    /// Seconds of audio sent to Whisper per inference tick.  Null = default.
    /// </summary>
    public int? WindowSeconds { get; set; }

    /// <summary>
    /// Seconds between inference ticks.  Null = default.  In Simple mode
    /// this is ignored (step equals window — no overlap).
    /// </summary>
    public int? StepSeconds { get; set; }

    // ── Channel (required — every job belongs to a channel) ───
    public Guid ChannelId { get; set; }
    public ChannelDB Channel { get; set; } = null!;

    public string? Language { get; set; }

    // ── Logs ──────────────────────────────────────────────────────
    public ICollection<AgentJobLogEntryDB> LogEntries { get; set; } = [];
    public ICollection<TranscriptionSegmentDB> TranscriptionSegments { get; set; } = [];
}
