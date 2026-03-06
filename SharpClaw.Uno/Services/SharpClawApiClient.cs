namespace SharpClaw.Services;

/// <summary>
/// HTTP client that communicates with the SharpClaw internal API
/// running on localhost. Reads the per-session API key from the
/// well-known file written by the API process.
/// </summary>
public sealed class SharpClawApiClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _cachedApiKey;

    public SharpClawApiClient(string baseUrl)
    {
        _http = new HttpClient
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
            }
            catch (HttpRequestException) { }
            catch (InvalidOperationException) { }
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
    /// Clears the cached API key so the next request re-reads from disk.
    /// Call this after restarting the API process.
    /// </summary>
    public void InvalidateApiKey() => _cachedApiKey = null;

    public void Dispose() => _http.Dispose();
}
