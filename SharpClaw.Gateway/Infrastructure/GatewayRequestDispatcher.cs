using System.Net;
using System.Text.Json;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Facade used by controllers to dispatch requests to the core API.
/// <list type="bullet">
///   <item><b>GET</b> requests are always forwarded directly via <see cref="InternalApiClient"/>.</item>
///   <item><b>POST / PUT / DELETE</b> (mutations) are routed through the
///     <see cref="RequestQueueService"/> for sequential processing when the
///     queue is enabled; otherwise they fall through to direct calls.</item>
/// </list>
/// </summary>
public sealed class GatewayRequestDispatcher(
    InternalApiClient coreApi,
    RequestQueueService queue,
    IHttpContextAccessor httpContextAccessor,
    ILogger<GatewayRequestDispatcher> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // ── READ (always direct) ─────────────────────────────────────

    /// <summary>Forwards a GET request directly to the core API.</summary>
    public Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
        => coreApi.GetAsync<T>(path, ct);

    // ── MUTATIONS (queued when enabled) ──────────────────────────

    /// <summary>Enqueues a POST and returns the deserialised response.</summary>
    public Task<QueuedResponse> PostAsync<TRequest>(
        string path, TRequest body, CancellationToken ct = default)
        => EnqueueOrDirectAsync(HttpMethod.Post, path, body, ct);

    /// <summary>Enqueues a POST with no body.</summary>
    public Task<QueuedResponse> PostAsync(string path, CancellationToken ct = default)
        => EnqueueOrDirectAsync(HttpMethod.Post, path, (object?)null, ct);

    /// <summary>Enqueues a PUT and returns the deserialised response.</summary>
    public Task<QueuedResponse> PutAsync<TRequest>(
        string path, TRequest body, CancellationToken ct = default)
        => EnqueueOrDirectAsync(HttpMethod.Put, path, body, ct);

    /// <summary>Enqueues a DELETE.</summary>
    public Task<QueuedResponse> DeleteAsync(string path, CancellationToken ct = default)
        => EnqueueOrDirectAsync(HttpMethod.Delete, path, (object?)null, ct);

    // ── Internals ────────────────────────────────────────────────

    private async Task<QueuedResponse> EnqueueOrDirectAsync<TRequest>(
        HttpMethod method, string path, TRequest? body, CancellationToken ct)
    {
        var jsonBody = body is not null ? JsonSerializer.Serialize(body, JsonOptions) : null;

        if (!queue.Enabled)
            return await DirectForwardAsync(method, path, jsonBody, ct);

        var request = new QueuedRequest
        {
            Method = method,
            Path = path,
            JsonBody = jsonBody,
            Priority = ResolvePriority(),
        };

        if (!queue.TryEnqueue(request))
        {
            httpContextAccessor.HttpContext?.Items["QueueFull"] = true;
            return new QueuedResponse
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                Error = "Request queue is full. Try again later.",
            };
        }

        // Register cancellation so if the HTTP request is aborted the
        // queued item's TCS is cancelled as well.
        await using var reg = ct.Register(() => request.Completion.TrySetCanceled(ct));

        var response = await request.Completion.Task;

        if (response.Meta is not null)
            httpContextAccessor.HttpContext?.Items["QueueMeta"] = response.Meta;

        return response;
    }

    private async Task<QueuedResponse> DirectForwardAsync(
        HttpMethod method, string path, string? jsonBody, CancellationToken ct)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(method, path);
            if (jsonBody is not null)
            {
                httpRequest.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8);
                httpRequest.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }

            using var response = await coreApi.SendRawAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return new QueuedResponse
            {
                StatusCode = response.StatusCode,
                JsonBody = responseBody,
                Error = response.IsSuccessStatusCode ? null : responseBody,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Direct forward failed for {Method} {Path}.", method, path);
            return new QueuedResponse
            {
                StatusCode = HttpStatusCode.BadGateway,
                Error = $"Core API unreachable: {ex.Message}",
            };
        }
    }

    private RequestPriority ResolvePriority()
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers["X-Priority"].FirstOrDefault();
        return header?.ToLowerInvariant() switch
        {
            "high" => RequestPriority.High,
            "low" => RequestPriority.Low,
            _ => RequestPriority.Normal,
        };
    }
}
