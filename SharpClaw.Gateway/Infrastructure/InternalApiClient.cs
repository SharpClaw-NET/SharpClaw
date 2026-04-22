using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SharpClaw.Utils.Logging;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Typed <see cref="HttpClient"/> wrapper that forwards requests to the
/// internal SharpClaw Application API, automatically attaching the
/// <c>X-Api-Key</c> header and forwarding the caller's JWT when present.
/// </summary>
public sealed class InternalApiClient(
    HttpClient httpClient,
    IOptions<InternalApiOptions> options,
    IHttpContextAccessor httpContextAccessor,
    SessionLogWriter sessionLogWriter)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private string? _cachedApiKey;
    private string? _cachedGatewayToken;

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AttachApiKey(request);
        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[gateway] 401 on GET {path} — body: {body}");
            sessionLogWriter.AppendDebug($"401 on GET {path} — body: {body}");

            if (TryInvalidateAndReAttach(request))
            {
                using var retry = CloneRequest(request);
                using var retryResp = await httpClient.SendAsync(retry, ct);
                retryResp.EnsureSuccessStatusCode();
                var retryJson = await retryResp.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<T>(retryJson, JsonOptions);
            }
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task<TResponse?> PostAsync<TRequest, TResponse>(
        string path, TRequest body, CancellationToken ct = default)
    {
        var bodyJson = JsonSerializer.Serialize(body, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AttachApiKey(request);
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && TryInvalidateAndReAttach(request))
        {
            using var retry = new HttpRequestMessage(HttpMethod.Post, path);
            AttachApiKey(retry);
            retry.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var retryResp = await httpClient.SendAsync(retry, ct);
            retryResp.EnsureSuccessStatusCode();
            var retryJson = await retryResp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<TResponse>(retryJson, JsonOptions);
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<TResponse>(json, JsonOptions);
    }

    public async Task PostAsync<TRequest>(string path, TRequest body, CancellationToken ct = default)
    {
        var bodyJson = JsonSerializer.Serialize(body, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AttachApiKey(request);
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && TryInvalidateAndReAttach(request))
        {
            using var retry = new HttpRequestMessage(HttpMethod.Post, path);
            AttachApiKey(retry);
            retry.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var retryResp = await httpClient.SendAsync(retry, ct);
            retryResp.EnsureSuccessStatusCode();
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<TResponse?> PutAsync<TRequest, TResponse>(
        string path, TRequest body, CancellationToken ct = default)
    {
        var bodyJson = JsonSerializer.Serialize(body, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        AttachApiKey(request);
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && TryInvalidateAndReAttach(request))
        {
            using var retry = new HttpRequestMessage(HttpMethod.Put, path);
            AttachApiKey(retry);
            retry.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var retryResp = await httpClient.SendAsync(retry, ct);
            retryResp.EnsureSuccessStatusCode();
            var retryJson = await retryResp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<TResponse>(retryJson, JsonOptions);
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<TResponse>(json, JsonOptions);
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        AttachApiKey(request);
        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && TryInvalidateAndReAttach(request))
        {
            using var retry = new HttpRequestMessage(HttpMethod.Delete, path);
            AttachApiKey(retry);
            using var retryResp = await httpClient.SendAsync(retry, ct);
            return retryResp.IsSuccessStatusCode;
        }

        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Sends a pre-built <see cref="HttpRequestMessage"/> with the API key
    /// attached. The caller owns the response (must dispose).
    /// Used by <see cref="RequestQueueProcessor"/> to forward queued requests.
    /// </summary>
    public async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request, CancellationToken ct = default)
    {
        AttachApiKey(request);
        return await httpClient.SendAsync(request, ct);
    }

    /// <summary>
    /// Clears the cached API key so the next request re-reads from disk
    /// or configuration. Call after the backend restarts with a new key.
    /// </summary>
    public void InvalidateApiKey() => _cachedApiKey = null;

    private void AttachApiKey(HttpRequestMessage request)
    {
        var key = ResolveApiKey();
        if (request.Headers.Contains("X-Api-Key"))
            request.Headers.Remove("X-Api-Key");
        request.Headers.Add("X-Api-Key", key);

        // Attach gateway service token (proves identity beyond the shared API key).
        var gatewayToken = ResolveGatewayToken();
        if (gatewayToken is not null)
        {
            if (request.Headers.Contains("X-Gateway-Token"))
                request.Headers.Remove("X-Gateway-Token");
            request.Headers.Add("X-Gateway-Token", gatewayToken);
        }

        // Forward the caller's Authorization header when serving a user request
        // (bot services run outside of an HTTP context and won't have one).
        var authHeader = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && !request.Headers.Contains("Authorization"))
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);

        Console.WriteLine(
            $"[gateway] {request.Method} {request.RequestUri} " +
            $"X-Api-Key: {key.Length} chars, " +
            $"prefix={key[..Math.Min(6, key.Length)]}.., " +
            $"suffix=..{key[^Math.Min(4, key.Length)..]} " +
            $"GwToken={gatewayToken?.Length.ToString() ?? "none"}");
        sessionLogWriter.AppendDebug(
            $"{request.Method} {request.RequestUri} X-Api-Key: {key.Length} chars, GwToken={gatewayToken?.Length.ToString() ?? "none"}");
    }

    /// <summary>
    /// On 401, clears the cached key and re-resolves it.
    /// Returns <c>true</c> if a different key was obtained (worth retrying).
    /// </summary>
    private bool TryInvalidateAndReAttach(HttpRequestMessage request)
    {
        var oldKey = _cachedApiKey;
        _cachedApiKey = null;

        try
        {
            var newKey = ResolveApiKey();
            return newKey != oldKey;
        }
        catch
        {
            // Key file missing or unreadable — nothing to retry with.
            return false;
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private string? ResolveGatewayToken()
    {
        if (_cachedGatewayToken is not null)
            return _cachedGatewayToken;

        var opts = options.Value;

        if (!string.IsNullOrEmpty(opts.GatewayToken))
        {
            _cachedGatewayToken = opts.GatewayToken;
            return _cachedGatewayToken;
        }

        // Read from the well-known file written by the internal API
        var tokenFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw", ".gateway-token");

        if (!File.Exists(tokenFilePath))
            return null;

        _cachedGatewayToken = File.ReadAllText(tokenFilePath).Trim();
        return _cachedGatewayToken;
    }

    private string ResolveApiKey()
    {
        if (_cachedApiKey is not null)
            return _cachedApiKey;

        var opts = options.Value;

        if (!string.IsNullOrEmpty(opts.ApiKey))
        {
            _cachedApiKey = opts.ApiKey;
            return _cachedApiKey;
        }

        // Read from the well-known file written by the internal API
        var keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw", ".api-key");

        if (!File.Exists(keyFilePath))
            throw new InvalidOperationException(
                $"Internal API key file not found at '{keyFilePath}'. " +
                "Ensure the internal SharpClaw API is running.");

        _cachedApiKey = File.ReadAllText(keyFilePath).Trim();
        return _cachedApiKey;
    }
}
