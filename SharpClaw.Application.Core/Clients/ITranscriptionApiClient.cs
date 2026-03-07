using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Provider-specific client for speech-to-text transcription.
/// Each implementation knows how to send audio data to its provider's
/// transcription endpoint and parse the result.
/// </summary>
public interface ITranscriptionApiClient
{
    ProviderType ProviderType { get; }

    /// <summary>
    /// Transcribes a chunk of audio data.
    /// </summary>
    /// <param name="httpClient">Shared HTTP client.</param>
    /// <param name="apiKey">Decrypted provider API key.</param>
    /// <param name="model">Model identifier (e.g. "whisper-1").</param>
    /// <param name="audioData">Raw audio bytes (WAV format).</param>
    /// <param name="language">BCP-47 language hint, or null for auto-detect.</param>
    /// <param name="prompt">
    /// Optional text prompt providing context for the model. Should contain
    /// the last few sentences of previously transcribed text to improve
    /// continuity across sliding-window boundaries.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<TranscriptionChunkResult> TranscribeAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        byte[] audioData,
        string? language = null,
        string? prompt = null,
        CancellationToken ct = default);
}

/// <summary>
/// Result of transcribing a single audio chunk.
/// </summary>
/// <param name="Language">
/// Language detected or confirmed by the model (e.g. "en"). Null when
/// the provider does not report it.
/// </param>
public sealed record TranscriptionChunkResult(
    string Text,
    double Duration,
    IReadOnlyList<TranscriptionChunkSegment> Segments,
    string? Language = null);

/// <summary>
/// A segment within a transcription chunk result.
/// </summary>
/// <param name="Text">Transcribed text for this segment.</param>
/// <param name="Start">Start time in seconds relative to the chunk.</param>
/// <param name="End">End time in seconds relative to the chunk.</param>
/// <param name="Confidence">
/// Confidence score (0–1). For OpenAI this is derived from
/// <c>avg_logprob</c>; for local Whisper it is the segment probability.
/// </param>
/// <param name="NoSpeechProbability">
/// Whisper's estimated probability that the segment contains no speech.
/// Higher values indicate the segment is likely silence or noise.
/// Null when the provider does not report this metric.
/// </param>
/// <param name="CompressionRatio">
/// Whisper's compression ratio for the segment. Values above ~2.4
/// often indicate hallucinated or repetitive text. Null when the
/// provider does not report this metric.
/// </param>
public sealed record TranscriptionChunkSegment(
    string Text,
    double Start,
    double End,
    double? Confidence,
    double? NoSpeechProbability = null,
    double? CompressionRatio = null);
