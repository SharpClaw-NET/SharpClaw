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
