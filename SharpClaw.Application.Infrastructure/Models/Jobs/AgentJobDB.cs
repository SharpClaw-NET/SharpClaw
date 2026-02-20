using SharpClaw.Application.Infrastructure.Models.Conversation;
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

    public Guid? ConversationId { get; set; }
    public ConversationDB? Conversation { get; set; }

    public string? Language { get; set; }

    // ── Logs ──────────────────────────────────────────────────────
    public ICollection<AgentJobLogEntryDB> LogEntries { get; set; } = [];
    public ICollection<TranscriptionSegmentDB> TranscriptionSegments { get; set; } = [];
}
