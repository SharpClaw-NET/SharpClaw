using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;
using SharpClaw.Modules.Transcription.Contracts;

namespace SharpClaw.Modules.Transcription.DTOs;

public sealed record TranscriptionSegmentResponse(
    Guid Id,
    string Text,
    double StartTime,
    double EndTime,
    double? Confidence,
    DateTimeOffset Timestamp,
    bool IsProvisional = false);

public sealed record PushSegmentRequest(
    string Text,
    double StartTime,
    double EndTime,
    double? Confidence = null);

public sealed record TranscriptionJobResponse(
    Guid Id,
    Guid ChannelId,
    Guid AgentId,
    string? ActionKey,
    Guid? ResourceId,
    AgentJobStatus Status,
    PermissionClearance EffectiveClearance,
    string? ResultData,
    string? ErrorLog,
    IReadOnlyList<AgentJobLogResponse> Logs,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid? TranscriptionModelId,
    string? Language,
    TranscriptionMode? TranscriptionMode,
    int? WindowSeconds,
    int? StepSeconds,
    IReadOnlyList<TranscriptionSegmentResponse> Segments,
    int TotalSegments,
    int FinalizedSegments,
    int ProvisionalSegments,
    double? TranscribedDurationSeconds,
    TokenUsageResponse? JobCost = null,
    ChannelCostResponse? ChannelCost = null);

public sealed record TranscriptionJobSummaryResponse(
    Guid Id,
    Guid ChannelId,
    Guid AgentId,
    string? ActionKey,
    Guid? ResourceId,
    AgentJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid? TranscriptionModelId,
    string? Language,
    TranscriptionMode? TranscriptionMode,
    int TotalSegments,
    int FinalizedSegments,
    int ProvisionalSegments,
    double? TranscribedDurationSeconds);
