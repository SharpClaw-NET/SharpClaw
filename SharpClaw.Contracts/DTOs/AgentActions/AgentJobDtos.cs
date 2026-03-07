using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.AgentActions;

// ── Requests ──────────────────────────────────────────────────────

public sealed record SubmitAgentJobRequest(
AgentActionType ActionType,
Guid? ResourceId = null,
Guid? AgentId = null,
Guid? CallerAgentId = null,
// Shell-specific
DangerousShellType? DangerousShellType = null,
SafeShellType? SafeShellType = null,
string? ScriptJson = null,
string? WorkingDirectory = null,
// Transcription-specific
Guid? TranscriptionModelId = null,
string? Language = null,
TranscriptionMode? TranscriptionMode = null,
int? WindowSeconds = null,
int? StepSeconds = null);

public sealed record ApproveAgentJobRequest(
    Guid? ApproverAgentId = null);

// ── Responses ─────────────────────────────────────────────────────

public sealed record AgentJobResponse(
Guid Id,
Guid ChannelId,
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
// Shell
DangerousShellType? DangerousShellType = null,
SafeShellType? SafeShellType = null,
string? ScriptJson = null,
string? WorkingDirectory = null,
// Transcription
Guid? TranscriptionModelId = null,
string? Language = null,
TranscriptionMode? TranscriptionMode = null,
int? WindowSeconds = null,
int? StepSeconds = null,
IReadOnlyList<TranscriptionSegmentResponse>? Segments = null);

public sealed record AgentJobLogResponse(
    string Message,
    string Level,
    DateTimeOffset Timestamp);

/// <summary>
/// Lightweight summary returned by the list-summaries endpoint.
/// Contains only the fields needed to populate a dropdown or list view —
/// no <c>ResultData</c>, <c>ErrorLog</c>, <c>Logs</c>, or <c>Segments</c>.
/// </summary>
public sealed record AgentJobSummaryResponse(
    Guid Id,
    Guid ChannelId,
    Guid AgentId,
    AgentActionType ActionType,
    Guid? ResourceId,
    AgentJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
