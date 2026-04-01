using System.Text.Json;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Shared helper for provider API clients that replaces bare
/// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> with
/// a version that surfaces the upstream error body.
/// </summary>
internal static class ProviderHttpExtensions
{
    /// <summary>
    /// Throws an <see cref="HttpRequestException"/> whose message includes
    /// the provider's error detail (e.g. "The model 'xyz' does not exist")
    /// instead of a bare HTTP status code.
    /// </summary>
    public static async Task EnsureSuccessOrThrowAsync(
        this HttpResponseMessage response, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? detail = null;
        string? rawBody = null;
        try
        {
            rawBody = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                using var doc = JsonDocument.Parse(rawBody);

                // OpenAI-compatible: { "error": { "message": "..." } }
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    if (err.TryGetProperty("message", out var msg))
                        detail = msg.GetString();
                    else if (err.ValueKind == JsonValueKind.String)
                        detail = err.GetString();
                }

                // Anthropic: { "error": { "message": "..." } } — same shape,
                // but also check top-level "message" as fallback.
                if (detail is null
                    && doc.RootElement.TryGetProperty("message", out var topMsg))
                {
                    detail = topMsg.GetString();
                }
            }
        }
        catch { /* JSON parse or read failure — fall back to raw body below */ }

        // Fallback: use the raw response body when structured extraction found nothing.
        detail ??= rawBody is { Length: > 0 }
            ? (rawBody.Length > 500 ? rawBody[..500] : rawBody)
            : null;

        var status = (int)response.StatusCode;
        var reason = response.ReasonPhrase;
        var message = detail is not null
            ? $"Provider API error {status} ({reason}): {detail}"
            : $"Provider API error {status} ({reason}).";

        throw new HttpRequestException(message, inner: null, response.StatusCode);
    }
}
