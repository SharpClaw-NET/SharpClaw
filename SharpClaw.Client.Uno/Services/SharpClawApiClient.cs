using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Modules;

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
    private readonly ClientActionDispatcher _clientActions;
    private readonly ClientActionContextSource _contextSource;
    private readonly bool _ownsHttp;
    private readonly string? _fixedApiKey;
    private string? _cachedApiKey;
    private int _disposed;

    public SharpClawApiClient(
        string baseUrl,
        ILogger<SharpClawApiClient> logger,
        FrontendInstanceService? frontendInstance,
        ClientActionDispatcher clientActions,
        ClientActionContextSource? contextSource = null)
    {
        _frontendInstance = frontendInstance;
        _logger = logger;
        _clientActions = clientActions ?? throw new ArgumentNullException(nameof(clientActions));
        _contextSource = contextSource ?? new ClientActionContextSource();
        _ownsHttp = true;
        _http = new HttpClient(new HttpLoggingHandler(new HttpClientHandler(), logger))
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(10)
        };

    }

    internal SharpClawApiClient(
        HttpClient http,
        ILogger<SharpClawApiClient> logger,
        ClientActionDispatcher clientActions,
        ClientActionContextSource contextSource,
        string? fixedApiKey = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clientActions = clientActions ?? throw new ArgumentNullException(nameof(clientActions));
        _contextSource = contextSource ?? throw new ArgumentNullException(nameof(contextSource));
        _ownsHttp = false;
        _fixedApiKey = fixedApiKey;
    }

    /// <summary>Base URL of the localhost API (e.g. http://127.0.0.1:48923).</summary>
    public string BaseUrl => _http.BaseAddress!.ToString();

    /// <summary>
    /// Changes the target API base URL and clears the cached API key.
    /// </summary>
    public async ValueTask UpdateBaseUrlAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var expectedVersion = _clientActions.GetStateVersion("client.api.target");
        await _clientActions.CommitStateAsync(
            "client.api.target",
            expectedVersion,
            _ =>
            {
                _http.BaseAddress = new Uri(baseUrl);
                _cachedApiKey = null;
                _frontendInstance?.RememberBackendBinding(
                    backendInstanceId: null,
                    baseUrl,
                    bindingKind: "configured");
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    public async Task<HttpResponseMessage> GetAsync(
        string path, CancellationToken ct = default)
        => await SendClientCommandAsync("GET", path, null, responseHeadersRead: false, ct);

    public async Task<HttpResponseMessage> PostAsync(
        string path, HttpContent? content, CancellationToken ct = default)
        => await SendClientCommandAsync("POST", path, content, responseHeadersRead: false, ct);

    public Task ConsumeStreamAsync(
        string method,
        string path,
        HttpContent? content,
        Func<HttpResponseMessage, CancellationToken, Task> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(consume);

        return _clientActions.RunCommandAsync(
            new ClientCommandInvocation(
                "http.stream",
                method,
                SafePath(new Uri(path, UriKind.RelativeOrAbsolute)),
                Guid.NewGuid(),
                path),
            async (invocation, actionToken) =>
            {
                using var request = new HttpRequestMessage(
                    new HttpMethod(invocation.Method),
                    invocation.EffectiveRequestTarget)
                {
                    Content = content,
                };
                AttachApiKey(request);
                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    actionToken);
                await consume(response, actionToken);
                return true;
            },
            cancellationToken).AsTask();
    }

    public async Task<HttpResponseMessage> PutAsync(
        string path, HttpContent? content, CancellationToken ct = default)
        => await SendClientCommandAsync("PUT", path, content, responseHeadersRead: false, ct);

    public async Task<HttpResponseMessage> DeleteAsync(
        string path, CancellationToken ct = default)
        => await SendClientCommandAsync("DELETE", path, null, responseHeadersRead: false, ct);

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
        ArgumentNullException.ThrowIfNull(request);
        return await _clientActions.RunCommandAsync(
            new ClientCommandInvocation(
                "http.send",
                request.Method.Method,
                SafePath(request.RequestUri),
                Guid.NewGuid(),
                request.RequestUri?.ToString()),
            async (invocation, actionToken) =>
            {
                request.Method = new HttpMethod(invocation.Method);
                request.RequestUri = new Uri(
                    invocation.EffectiveRequestTarget,
                    UriKind.RelativeOrAbsolute);
                AttachApiKey(request);
                return await _http.SendAsync(request, actionToken);
            },
            ct);
    }

    private Task<HttpResponseMessage> SendClientCommandAsync(
        string method,
        string path,
        HttpContent? content,
        bool responseHeadersRead,
        CancellationToken cancellationToken)
    {
        return _clientActions.RunCommandAsync(
            new ClientCommandInvocation(
                "http.send",
                method,
                SafePath(new Uri(path, UriKind.RelativeOrAbsolute)),
                Guid.NewGuid(),
                path),
            async (invocation, actionToken) =>
            {
                using var request = new HttpRequestMessage(
                    new HttpMethod(invocation.Method),
                    invocation.EffectiveRequestTarget)
                {
                    Content = content,
                };
                AttachApiKey(request);
                return await _http.SendAsync(
                    request,
                    responseHeadersRead
                        ? HttpCompletionOption.ResponseHeadersRead
                        : HttpCompletionOption.ResponseContentRead,
                    actionToken);
            },
            cancellationToken).AsTask();
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
                    await InvalidateApiKeyAsync(cts.Token);
            }
            catch (HttpRequestException) { }
            catch (InvalidOperationException) { await InvalidateApiKeyAsync(cts.Token); }
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
    internal async ValueTask SetAccessTokenAsync(
        string? token,
        CancellationToken cancellationToken = default,
        ClientActionRequestContext? authenticatedContext = null)
    {
        var expectedVersion = _clientActions.GetStateVersion("client.auth");
        await _clientActions.CommitStateAsync(
            "client.auth",
            expectedVersion,
            _ =>
            {
                _accessToken = token;
                if (token is null)
                    _contextSource.ClearSession();
                else if (authenticatedContext is not null)
                    _contextSource.SetSession(authenticatedContext);
                else
                    _contextSource.ClearSession();
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    // The normal path uses client.auth. This local reset is only for a failed
    // cleanup action, so an authority failure cannot leave a usable token.
    internal void ForceClearSessionAfterActionFailure()
    {
        _accessToken = null;
        _contextSource.ClearSession();
    }

    /// <summary>Current access token, if any.</summary>
    public string? AccessToken => _accessToken;

    private string ResolveApiKey()
    {
        if (_fixedApiKey is not null)
            return _fixedApiKey;

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
    public async ValueTask InvalidateApiKeyAsync(
        CancellationToken cancellationToken = default)
    {
        var expectedVersion = _clientActions.GetStateVersion("client.api.key");
        await _clientActions.CommitStateAsync(
            "client.api.key",
            expectedVersion,
            _ =>
            {
                _cachedApiKey = null;
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    public void Dispose() =>
        DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _clientActions.RunCommandAsync(
            "client.api.dispose",
            _ =>
            {
                if (_ownsHttp)
                    _http.Dispose();
                return ValueTask.CompletedTask;
            });
    }

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
