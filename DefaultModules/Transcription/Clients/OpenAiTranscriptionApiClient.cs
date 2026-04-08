using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

using SharpClaw.Application.Core.Clients;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Modules.Transcription.Clients;

/// <summary>
/// Sends audio to the OpenAI <c>/v1/audio/transcriptions</c> endpoint
/// (Whisper). Works with any OpenAI-compatible transcription API.
/// </summary>
public class OpenAiTranscriptionApiClient : ITranscriptionApiClient
{
    protected virtual string ApiEndpoint => "https://api.openai.com/v1";

    public virtual ProviderType ProviderType => ProviderType.OpenAI;

    private const int MaxRetries = 3;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The <c>gpt-4o-*-transcribe</c> family only supports <c>json</c> or <c>text</c>,
    /// not <c>verbose_json</c>. Fall back to <c>json</c> for those models.
    /// </summary>
    private static string ResolveResponseFormat(string model) =>
        model.Contains("transcribe", StringComparison.OrdinalIgnoreCase)
            ? "json"
            : "verbose_json";

    public async Task<TranscriptionChunkResult> TranscribeAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        byte[] audioData,
        string? language = null,
        string? prompt = null,
        CancellationToken ct = default)
    {
        // Ensure audio is mono 16 kHz 16-bit PCM — optimal for Whisper / ASR.
        // The WASAPI capture path already outputs this format so the fast
        // path returns the bytes unchanged; future file/stream inputs get
        // resampled here automatically.
        audioData = AudioNormalizer.Normalize(audioData);

        HttpResponseMessage? response = null;

        for (var attempt = 0; ; attempt++)
        {
            using var content = new MultipartFormDataContent();

            var audioContent = new ByteArrayContent(audioData);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "file", "chunk.wav");
            content.Add(new StringContent(model), "model");
            content.Add(new StringContent(ResolveResponseFormat(model)), "response_format");

            if (language is not null)
                content.Add(new StringContent(language), "language");

            // Prompt conditioning: Whisper uses the last ~224 tokens of
            // the prompt to maintain style, vocabulary, and continuity
            // across sliding-window boundaries.
            if (prompt is not null)
                content.Add(new StringContent(prompt), "prompt");

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{ApiEndpoint}/audio/transcriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = content;

            response?.Dispose();
            response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Distinguish permanent quota exhaustion from transient rate-limits.
                // Retrying won't help when the account has no credits left.
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                if (errorBody.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException(
                        $"OpenAI quota exhausted (insufficient_quota). " +
                        $"Check your plan and billing at https://platform.openai.com/account/billing. " +
                        $"Response: {errorBody}",
                        inner: null,
                        response.StatusCode);
                }

                if (attempt < MaxRetries)
                {
                    var delay = InitialBackoff * Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                    continue;
                }
            }

            // Any non-success status besides 429 is non-retryable.
            // Read the body so the exception includes the API error message.
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Transcription API error ({(int)response.StatusCode} {response.StatusCode}): {errorBody}",
                    inner: null,
                    response.StatusCode);
            }

            break;
        }

        using (response)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<WhisperVerboseResponse>(json)
                    ?? throw new InvalidOperationException("Empty transcription response.");

                // The gpt-4o-*-transcribe models only support "json" format which
                // omits duration, segments, and language.  Estimate from the WAV
                // audio length so downstream timestamp logic works correctly.
                var effectiveDuration = result.Duration > 0
                    ? result.Duration
                    : EstimateWavDuration(audioData);

                var segments = result.Segments?
                    .Select(s => new TranscriptionChunkSegment(
                        s.Text?.Trim() ?? "",
                        s.Start,
                        s.End,
                        s.AvgLogprob.HasValue ? Math.Exp(s.AvgLogprob.Value) : null,
                        s.NoSpeechProb,
                        s.CompressionRatio))
                    .ToList()
                    ?? [];

                // Track whether the API returned real per-segment timestamps.
                // When false the orchestrator uses text-diff dedup instead of
                // time-overlap dedup for the sliding-window pipeline.
                var hasTimestampedSegments = segments.Count > 0;

                // If no segments were returned, create one from the full text
                if (segments.Count == 0 && !string.IsNullOrWhiteSpace(result.Text))
                {
                    segments.Add(new TranscriptionChunkSegment(
                        result.Text.Trim(), 0, effectiveDuration, null));
                }

                return new TranscriptionChunkResult(
                    result.Text?.Trim() ?? "", effectiveDuration, segments,
                    result.Language, hasTimestampedSegments);
            }
    }

    // ── Whisper verbose_json response DTO ─────────────────────────

    private sealed record WhisperVerboseResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("segments")] List<WhisperSegment>? Segments);

    private sealed record WhisperSegment(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("avg_logprob")] double? AvgLogprob,
        [property: JsonPropertyName("no_speech_prob")] double? NoSpeechProb,
        [property: JsonPropertyName("compression_ratio")] double? CompressionRatio);

    /// <summary>
    /// Estimates the duration of a WAV audio byte array when the API
    /// response doesn't include it (e.g. <c>gpt-4o-transcribe</c> in
    /// <c>json</c> mode).  Assumes standard RIFF WAV with a 44-byte
    /// header; falls back to the full length if the header is absent.
    /// </summary>
    private static double EstimateWavDuration(byte[] audioData)
    {
        if (audioData.Length < 44)
            return 0;

        // Parse from the WAV header for accuracy.
        // Bytes 24-27: sample rate (uint32 LE)
        // Bytes 32-33: block align (uint16 LE) = channels × bitsPerSample / 8
        var sampleRate = BitConverter.ToUInt32(audioData, 24);
        var blockAlign = BitConverter.ToUInt16(audioData, 32);

        if (sampleRate == 0 || blockAlign == 0)
            return 0;

        var dataBytes = audioData.Length - 44;
        return (double)dataBytes / (sampleRate * blockAlign);
    }
}
