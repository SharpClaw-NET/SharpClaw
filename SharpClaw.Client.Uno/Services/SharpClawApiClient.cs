using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Services;

/// <summary>
/// HTTP client that communicates with the selected SharpClaw internal API
/// and resolves its per-session API key from backend discovery metadata.
/// </summary>
public sealed class SharpClawApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly FrontendInstanceService? _frontendInstance;
    private readonly ILogger<SharpClawApiClient> _logger;
    private string? _cachedApiKey;

    public SharpClawApiClient(
        string baseUrl,
        ILogger<SharpClawApiClient> logger,
        FrontendInstanceService? frontendInstance = null)
    {
        _frontendInstance = frontendInstance;
        _logger = logger;
        _http = new HttpClient(new HttpLoggingHandler(new HttpClientHandler(), logger))
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(10)
        };

    }

    /// <summary>Base URL of the localhost API (e.g. http://127.0.0.1:48923).</summary>
    public string BaseUrl => _http.BaseAddress!.ToString();

    /// <summary>
    /// Changes the target API base URL and clears the cached API key.
    /// </summary>
    public void UpdateBaseUrl(string baseUrl)
    {
        _http.BaseAddress = new Uri(baseUrl);
        _cachedApiKey = null;
        _frontendInstance?.RememberBackendBinding(
            backendInstanceId: null,
            baseUrl,
            bindingKind: "configured");
    }

    public async Task<HttpResponseMessage> GetAsync(
        string path, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        AttachApiKey(request);
        return await _http.SendAsync(request, ct);
    }

    public async Task<HttpResponseMessage> PostAsync(
        string path, HttpContent? content, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        AttachApiKey(request);
        return await _http.SendAsync(request, ct);
    }

    public async Task<HttpResponseMessage> PostStreamAsync(
        string path, HttpContent? content, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        AttachApiKey(request);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task<HttpResponseMessage> GetStreamAsync(
        string path, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        AttachApiKey(request);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task<HttpResponseMessage> PutAsync(
        string path, HttpContent? content, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        AttachApiKey(request);
        return await _http.SendAsync(request, ct);
    }

    public async Task<HttpResponseMessage> DeleteAsync(
        string path, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        AttachApiKey(request);
        return await _http.SendAsync(request, ct);
    }

    /// <summary>
    /// GET + deserialize a JSON list, swallowing errors and returning <c>null</c> on failure.
    /// </summary>
    public async Task<List<T>?> FetchListAsync<T>(string path, JsonSerializerOptions json, CancellationToken ct = default)
    {
        try
        {
            using var resp = await GetAsync(path, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var s = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonSerializer.DeserializeAsync<List<T>>(s, json, ct);
            }
        }
        catch { /* swallow */ }
        return null;
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct = default)
    {
        AttachApiKey(request);
        return await _http.SendAsync(request, ct);
    }

    /// <summary>
    /// Waits for the API process to become reachable and the API key to be
    /// valid by polling the <c>/ping</c> endpoint (requires X-Api-Key).
    /// </summary>
    public async Task WaitForReadyAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var response = await GetAsync("/ping", cts.Token);
                if (response.IsSuccessStatusCode)
                    return;

                // API key mismatch — the API process may have restarted
                // and written a new key to disk.  Clear the cache so the
                // next attempt re-reads the file.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    InvalidateApiKey();
            }
            catch (HttpRequestException) { }
            catch (InvalidOperationException) { InvalidateApiKey(); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }

            await Task.Delay(250, cts.Token);
        }

        throw new TimeoutException(
            $"SharpClaw API did not become reachable at {BaseUrl} within {timeout}.");
    }

    private void AttachApiKey(HttpRequestMessage request)
    {
        var key = ResolveApiKey();
        request.Headers.Add("X-Api-Key", key);

        if (_accessToken is not null)
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private string? _accessToken;

    /// <summary>
    /// Stores the JWT access token returned by <c>/auth/login</c>.
    /// Subsequent requests include it as a Bearer token.
    /// </summary>
    public void SetAccessToken(string token) => _accessToken = token;

    /// <summary>Current access token, if any.</summary>
    public string? AccessToken => _accessToken;

    private string ResolveApiKey()
    {
        if (_cachedApiKey is not null)
            return _cachedApiKey;

        var keyFilePath = _frontendInstance?.ResolveBackendApiKeyPath(BaseUrl);

        if (string.IsNullOrWhiteSpace(keyFilePath) || !File.Exists(keyFilePath))
            throw new InvalidOperationException(
                $"API key file could not be resolved for backend '{BaseUrl}'. " +
                "Ensure the selected SharpClaw backend is running and has published discovery metadata.");

        _cachedApiKey = File.ReadAllText(keyFilePath).Trim();
        return _cachedApiKey;
    }

    /// <summary>
    /// The currently cached API key, or <c>null</c> if not yet resolved.
    /// Used to forward the verified key to child processes (e.g. gateway)
    /// without file I/O that may break under MSIX VFS virtualisation.
    /// </summary>
    public string? CachedApiKey => _cachedApiKey;

    /// <summary>
    /// Clears the cached API key so the next request re-reads from disk.
    /// Call this after restarting the API process.
    /// </summary>
    public void InvalidateApiKey() => _cachedApiKey = null;

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Emits bounded request metadata through the process logger. Bodies and
    /// credential-bearing headers are intentionally never collected.
    /// </summary>
    private sealed class HttpLoggingHandler(
        HttpMessageHandler inner,
        ILogger logger) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var path = SafePath(request.RequestUri);
            logger.LogDebug(
                "HTTP request {RequestId} started: {Method} {Path}; content length={ContentLength}",
                id,
                request.Method,
                path,
                request.Content?.Headers.ContentLength);

            var sw = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogError(
                    ex,
                    "HTTP request {RequestId} failed after {ElapsedMilliseconds}ms: {Method} {Path}",
                    id,
                    sw.ElapsedMilliseconds,
                    request.Method,
                    path);
                throw;
            }
            sw.Stop();

            logger.LogInformation(
                "HTTP request {RequestId} completed: {StatusCode} after {ElapsedMilliseconds}ms: {Method} {Path}; response length={ContentLength}",
                id,
                (int)response.StatusCode,
                sw.ElapsedMilliseconds,
                request.Method,
                path,
                response.Content?.Headers.ContentLength);

            return response;
        }
    }

    private static string SafePath(Uri? uri)
    {
        if (uri is null)
            return string.Empty;
        return uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
    }
}
