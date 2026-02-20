using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Conversation;

/// <summary>
/// A single segment of transcribed text produced by a live
/// transcription job.
/// </summary>
public class TranscriptionSegmentDB : BaseEntity
{
    public Guid AgentJobId { get; set; }
    public AgentJobDB AgentJob { get; set; } = null!;

    /// <summary>The transcribed text for this segment.</summary>
    public required string Text { get; set; }

    /// <summary>Start time offset in seconds from the beginning of the audio stream.</summary>
    public double StartTime { get; set; }

    /// <summary>End time offset in seconds from the beginning of the audio stream.</summary>
    public double EndTime { get; set; }

    /// <summary>Confidence score (0.0 – 1.0) if provided by the transcription engine.</summary>
    public double? Confidence { get; set; }

    /// <summary>Wall-clock time when this segment was recognised.</summary>
    public DateTimeOffset Timestamp { get; set; }
}
