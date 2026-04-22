using System.Diagnostics;
using System.Text.Json;
using SharpClaw.Utils.Logging;

namespace SharpClaw.Services;

/// <summary>
/// HTTP client that communicates with the SharpClaw internal API
/// running on localhost. Reads the per-session API key from the
/// well-known file written by the API process.
/// </summary>
public sealed class SharpClawApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SessionLogWriter? _sessionLogWriter;
    private string? _cachedApiKey;

    public SharpClawApiClient(string baseUrl, SessionLogWriter? sessionLogWriter = null)
    {
        _sessionLogWriter = sessionLogWriter;
        _http = new HttpClient(new DebugLoggingHandler(new HttpClientHandler(), sessionLogWriter))
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

        var keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw", ".api-key");

        if (!File.Exists(keyFilePath))
            throw new InvalidOperationException(
                $"API key file not found at '{keyFilePath}'. " +
                "Ensure the SharpClaw API process is running.");

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
    /// Logs full HTTP request/response details to <see cref="Debug.WriteLine(string, string)"/>
    /// under the <c>SharpClaw.Uno</c> category so they appear in the
    /// Visual Studio <b>Output › Debug</b> pane when attached.
    /// </summary>
    private sealed class DebugLoggingHandler(
        HttpMessageHandler inner,
        SessionLogWriter? sessionLogWriter) : DelegatingHandler(inner)
    {
        private const string Category = "SharpClaw.Uno";

        [Conditional("DEBUG")]
        private static void Log(string message) => Debug.WriteLine(message, Category);

        private void LogPersistent(string message)
        {
            Log(message);
            sessionLogWriter?.AppendDebug(message);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N")[..8];

            LogPersistent($"[{id}] >>> {request.Method} {request.RequestUri}");

            if (request.Content is not null)
            {
                var contentType = request.Content.Headers.ContentType?.MediaType ?? "";
                if (IsTextContent(contentType))
                {
                    var body = await request.Content.ReadAsStringAsync(cancellationToken);
                    LogPersistent($"[{id}] >>> Body:\n{body}");
                }
                else
                {
                    LogPersistent($"[{id}] >>> Body: <binary {contentType}, {request.Content.Headers.ContentLength} bytes>");
                }
            }

            var sw = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogPersistent($"[{id}] !!! FAILED after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
                sessionLogWriter?.AppendException(ex, $"HTTP request failed: {request.Method} {request.RequestUri}");
                throw;
            }
            sw.Stop();

            LogPersistent($"[{id}] <<< {(int)response.StatusCode} {response.ReasonPhrase} ({sw.ElapsedMilliseconds}ms)");

            if (response.Content is not null)
            {
                var responseContentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (IsTextContent(responseContentType)
                    && responseContentType is not "text/event-stream")
                {
                    // Skip full body read for large responses to avoid buffering
                    // multi-MB payloads (e.g. job details with base64 screenshots)
                    var contentLength = response.Content.Headers.ContentLength;
                    if (contentLength is not null and > 4096)
                    {
                        LogPersistent($"[{id}] <<< Body: <{responseContentType}, {contentLength:N0} bytes>");
                    }
                    else
                    {
                        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        LogPersistent($"[{id}] <<< Body:\n{responseBody}");
                    }
                }
                else
                {
                    LogPersistent($"[{id}] <<< Body: <{responseContentType}, {response.Content.Headers.ContentLength} bytes>");
                }
            }

            return response;
        }

        private static bool IsTextContent(string mediaType) =>
            mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }
}
