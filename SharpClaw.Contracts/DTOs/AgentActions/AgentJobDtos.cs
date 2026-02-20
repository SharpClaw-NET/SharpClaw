using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.AgentActions;

// ── Requests ──────────────────────────────────────────────────────

public sealed record SubmitAgentJobRequest(
AgentActionType ActionType,
Guid? ResourceId = null,
Guid? CallerUserId = null,
Guid? CallerAgentId = null,
// Transcription-specific
Guid? TranscriptionModelId = null,
Guid? ConversationId = null,
string? Language = null);

public sealed record ApproveAgentJobRequest(
    Guid? ApproverUserId = null,
    Guid? ApproverAgentId = null);

// ── Responses ─────────────────────────────────────────────────────

public sealed record AgentJobResponse(
    Guid Id,
    Guid AgentId,
    AgentActionType ActionType,
    Guid? ResourceId,
    AgentJobStatus Status,
    PermissionClearance EffectiveClearance,
    string? ResultData,
    string? ErrorLog,
    IReadOnlyList<AgentJobLogResponse> Logs,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    // Transcription
    Guid? TranscriptionModelId = null,
    Guid? ConversationId = null,
    string? Language = null,
    IReadOnlyList<TranscriptionSegmentResponse>? Segments = null);

public sealed record AgentJobLogResponse(
    string Message,
    string Level,
    DateTimeOffset Timestamp);
