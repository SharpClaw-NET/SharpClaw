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
    /// <param name="ct">Cancellation token.</param>
    Task<TranscriptionChunkResult> TranscribeAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        byte[] audioData,
        string? language = null,
        CancellationToken ct = default);
}

/// <summary>
/// Result of transcribing a single audio chunk.
/// </summary>
public sealed record TranscriptionChunkResult(
    string Text,
    double Duration,
    IReadOnlyList<TranscriptionChunkSegment> Segments);

/// <summary>
/// A segment within a transcription chunk result.
/// </summary>
public sealed record TranscriptionChunkSegment(
    string Text,
    double Start,
    double End,
    double? Confidence);
