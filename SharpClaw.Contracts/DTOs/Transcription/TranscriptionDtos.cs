using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Transcription;

// ── Input Audio DTOs ─────────────────────────────────────────────

public sealed record CreateInputAudioRequest(
    string Name,
    string? DeviceIdentifier = null,
    string? Description = null);

public sealed record UpdateInputAudioRequest(
    string? Name = null,
    string? DeviceIdentifier = null,
    string? Description = null);

public sealed record InputAudioResponse(
    Guid Id,
    string Name,
    string? DeviceIdentifier,
    string? Description,
    Guid? SkillId,
    DateTimeOffset CreatedAt);

public sealed record InputAudioSyncResult(
    int Imported,
    int Skipped,
    IReadOnlyList<string> ImportedNames,
    IReadOnlyList<string> SkippedNames);

// ── Transcription Segment DTOs ────────────────────────────────────

public sealed record TranscriptionSegmentResponse(
    Guid Id,
    string Text,
    double StartTime,
    double EndTime,
    double? Confidence,
    DateTimeOffset Timestamp,
    bool IsProvisional = false);

/// <summary>
/// Request body for pushing a transcription segment from an external
/// transcription engine or audio processor.
/// </summary>
public sealed record PushSegmentRequest(
    string Text,
    double StartTime,
    double EndTime,
    double? Confidence = null);

// ── Transcription Job DTO ─────────────────────────────────────────

/// <summary>
/// A transcription-focused view of an agent job.  Contains the core job
/// fields plus transcription-specific parameters, segments, and computed
/// statistics.  Returned by the module's <c>/transcription/jobs</c>
/// endpoints so consumers get a clean shape without unrelated shell
/// fields.
/// </summary>
public sealed record TranscriptionJobResponse(
    // ── Core job fields ───────────────────────────────────────────
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
    // ── Transcription parameters ──────────────────────────────────
    Guid? TranscriptionModelId,
    string? Language,
    TranscriptionMode? TranscriptionMode,
    int? WindowSeconds,
    int? StepSeconds,
    // ── Segments ──────────────────────────────────────────────────
    IReadOnlyList<TranscriptionSegmentResponse> Segments,
    // ── Computed statistics ───────────────────────────────────────
    int TotalSegments,
    int FinalizedSegments,
    int ProvisionalSegments,
    double? TranscribedDurationSeconds,
    // ── Cost ──────────────────────────────────────────────────────
    TokenUsageResponse? JobCost = null,
    ChannelCostResponse? ChannelCost = null);

/// <summary>
/// Lightweight summary for transcription job lists — no segments or
/// heavy payloads.
/// </summary>
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
