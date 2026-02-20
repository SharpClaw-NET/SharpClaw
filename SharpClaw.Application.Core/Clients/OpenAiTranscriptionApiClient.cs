using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

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

    public async Task<TranscriptionChunkResult> TranscribeAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        byte[] audioData,
        string? language = null,
        CancellationToken ct = default)
    {
        HttpResponseMessage? response = null;

        for (var attempt = 0; ; attempt++)
        {
            using var content = new MultipartFormDataContent();

            var audioContent = new ByteArrayContent(audioData);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "file", "chunk.wav");
            content.Add(new StringContent(model), "model");
            content.Add(new StringContent("verbose_json"), "response_format");

            if (language is not null)
                content.Add(new StringContent(language), "language");

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

            response.EnsureSuccessStatusCode();
            break;
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<WhisperVerboseResponse>(json)
                ?? throw new InvalidOperationException("Empty transcription response.");

            var segments = result.Segments?
                .Select(s => new TranscriptionChunkSegment(
                    s.Text?.Trim() ?? "",
                    s.Start,
                    s.End,
                    s.AvgLogprob.HasValue ? Math.Exp(s.AvgLogprob.Value) : null))
                .ToList()
                ?? [];

            // If no segments were returned, create one from the full text
            if (segments.Count == 0 && !string.IsNullOrWhiteSpace(result.Text))
            {
                segments.Add(new TranscriptionChunkSegment(
                    result.Text.Trim(), 0, result.Duration, null));
            }

            return new TranscriptionChunkResult(
                result.Text?.Trim() ?? "", result.Duration, segments);
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
        [property: JsonPropertyName("avg_logprob")] double? AvgLogprob);
}
