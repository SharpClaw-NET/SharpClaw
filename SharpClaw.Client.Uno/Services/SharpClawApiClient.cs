using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;

/// <summary>
/// HTTP client that communicates with the selected SharpClaw internal API
/// and resolves its per-session API key from backend discovery metadata.
/// </summary>
public sealed class SharpClawApiClient : IDisposable, IAsyncDisposable
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "DisposeOwnedHttpAsync owns one physical release task awaited by the disposal terminal and receipt settlement; the supplied client is released only when its constructor explicitly transfers ownership.")]
    private readonly HttpClient _http;
    private readonly Lock _targetLock = new();
    private readonly FrontendInstanceService? _frontendInstance;
    private readonly ILogger<SharpClawApiClient> _logger;
    private readonly ClientActionDispatcher _clientActions;
    private readonly bool _ownsHttp;
    private readonly string? _fixedApiKey;
    private Uri _targetBaseUri;
    private string? _cachedApiKey;
    private Uri? _cachedApiKeyTarget;
    private readonly Lock _disposeLock = new();
    private Task? _disposeTask;
    private Task? _ownedHttpDisposalTask;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
    public SharpClawApiClient(
        string baseUrl,
        ILogger<SharpClawApiClient> logger,
        FrontendInstanceService? frontendInstance,
        ClientActionDispatcher clientActions)
    {
        _frontendInstance = frontendInstance;
        _logger = logger;
        _clientActions = clientActions ?? throw new ArgumentNullException(nameof(clientActions));
        _ownsHttp = true;
        _targetBaseUri = CreateTargetUri(baseUrl);
        _http = CreateHttpClient(logger);

    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "The returned HttpClient owns its handler through disposeHandler: true. Every constructor/configuration failure disposes the acquired handler/client before rethrowing; the analyzer cannot follow this explicit ownership transfer.")]
    private static HttpClient CreateHttpClient(ILogger logger)
    {
        var inner = new HttpClientHandler();
        HttpLoggingHandler? loggingHandler = null;
        HttpClient? http = null;
        try
        {
            loggingHandler = new HttpLoggingHandler(inner, logger);
            http = new HttpClient(loggingHandler, disposeHandler: true);
            http.Timeout = TimeSpan.FromMinutes(10);
            return http;
        }
        catch
        {
            if (http is not null) http.Dispose();
            else if (loggingHandler is not null) loggingHandler.Dispose();
            else inner.Dispose();
            throw;
        }
    }

    internal SharpClawApiClient(
        HttpClient http,
        ILogger<SharpClawApiClient> logger,
        ClientActionDispatcher clientActions,
        string? fixedApiKey = null,
        FrontendInstanceService? frontendInstance = null,
        bool ownsHttp = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clientActions = clientActions ?? throw new ArgumentNullException(nameof(clientActions));
        _frontendInstance = frontendInstance;
        // Ownership transfers only after this constructor successfully validates
        // the supplied transport; existing internal callers keep borrowing it.
        _ownsHttp = ownsHttp;
        _fixedApiKey = fixedApiKey;
        _targetBaseUri = http.BaseAddress
            ?? throw new ArgumentException(
                "The supplied HTTP client must have a base address.",
                nameof(http));
    }

    /// <summary>Base URL of the localhost API (e.g. http://127.0.0.1:48923).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
    public string BaseUrl
    {
        get
        {
            lock (_targetLock)
                return _targetBaseUri.ToString();
        }
    }

    /// <summary>
    /// Changes the target API base URL and clears the cached API key.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
    public async ValueTask UpdateBaseUrlAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var targetBaseUri = CreateTargetUri(baseUrl);
        var expectedVersion = _clientActions.GetStateVersion("client.api.target");
        await _clientActions.CommitStateAsync(
            "client.api.target",
            expectedVersion,
            _ =>
            {
                lock (_targetLock)
                {
                    _frontendInstance?.RememberBackendBinding(
                        backendInstanceId: null,
                        baseUrl,
                        bindingKind: "configured");
                    _targetBaseUri = targetBaseUri;
                    _cachedApiKey = null;
                    _cachedApiKeyTarget = null;
                }
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(true);
    }

    public async Task<HttpResponseMessage> GetAsync(
        string path, CancellationToken ct = default)
        => await SendClientCommandAsync("GET", path, null, responseHeadersRead: false, ct).ConfigureAwait(true);

    public async Task<HttpResponseMessage> PostAsync(
        string path, HttpContent? content, CancellationToken ct = default)
        => await SendClientCommandAsync("POST", path, content, responseHeadersRead: false, ct).ConfigureAwait(true);

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
        var targetBaseUri = GetTargetSnapshot();

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
                    ResolveRequestUri(targetBaseUri, invocation.EffectiveRequestTarget))
                {
                    Content = content,
                };
                AttachApiKey(request, targetBaseUri);
                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    actionToken).ConfigureAwait(true);
                await consume(response, actionToken).ConfigureAwait(true);
                return true;
            },
            cancellationToken).AsTask();
    }

    public async Task<HttpResponseMessage> PutAsync(
        string path, HttpContent? content, CancellationToken ct = default)
        => await SendClientCommandAsync("PUT", path, content, responseHeadersRead: false, ct).ConfigureAwait(true);

    public async Task<HttpResponseMessage> DeleteAsync(
        string path, CancellationToken ct = default)
        => await SendClientCommandAsync("DELETE", path, null, responseHeadersRead: false, ct).ConfigureAwait(true);

    /// <summary>
    /// GET + deserialize a JSON list, swallowing errors and returning <c>null</c> on failure.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0016",
        Justification = "The existing public Task<List<T>?> return type is retained for source and binary compatibility; changing generic task covariance is a breaking contract change.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This existing optional-list API explicitly returns null on any transport, action or deserialization failure; the fault type is observed in the bounded journal and is never represented as a successful list.")]
    public async Task<List<T>?> FetchListAsync<T>(string path, JsonSerializerOptions json, CancellationToken ct = default)
    {
        try
        {
            using var resp = await GetAsync(path, ct).ConfigureAwait(true);
            if (resp.IsSuccessStatusCode)
            {
                using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(true);
                return await JsonSerializer.DeserializeAsync<List<T>>(s, json, ct).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
        return null;
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetBaseUri = GetTargetSnapshot();
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
                request.RequestUri = ResolveRequestUri(
                    targetBaseUri,
                    invocation.EffectiveRequestTarget);
                AttachApiKey(request, targetBaseUri);
                return await _http.SendAsync(request, actionToken).ConfigureAwait(true);
            },
            ct).ConfigureAwait(true);
    }

    private Task<HttpResponseMessage> SendClientCommandAsync(
        string method,
        string path,
        HttpContent? content,
        bool responseHeadersRead,
        CancellationToken cancellationToken)
    {
        var targetBaseUri = GetTargetSnapshot();
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
                    ResolveRequestUri(targetBaseUri, invocation.EffectiveRequestTarget))
                {
                    Content = content,
                };
                AttachApiKey(request, targetBaseUri);
                return await _http.SendAsync(
                    request,
                    responseHeadersRead
                        ? HttpCompletionOption.ResponseHeadersRead
                        : HttpCompletionOption.ResponseContentRead,
                    actionToken).ConfigureAwait(true);
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
                using var response = await GetAsync("/ping", cts.Token).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                    return;

                // API key mismatch — the API process may have restarted
                // and written a new key to disk.  Clear the cache so the
                // next attempt re-reads the file.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    await InvalidateApiKeyAsync(cts.Token).ConfigureAwait(true);
            }
            catch (HttpRequestException) { }
            catch (InvalidOperationException) { await InvalidateApiKeyAsync(cts.Token).ConfigureAwait(true); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }

            await Task.Delay(250, cts.Token).ConfigureAwait(true);
        }

        throw new TimeoutException(
            $"SharpClaw API did not become reachable at {BaseUrl} within {timeout}.");
    }

    private void AttachApiKey(HttpRequestMessage request, Uri targetBaseUri)
    {
        var key = ResolveApiKey(targetBaseUri);
        request.Headers.Add("X-Api-Key", key);
    }

    private string ResolveApiKey(Uri targetBaseUri)
    {
        if (_fixedApiKey is not null)
            return _fixedApiKey;

        lock (_targetLock)
        {
            if (_cachedApiKey is not null && _cachedApiKeyTarget == targetBaseUri)
                return _cachedApiKey;
        }

        var targetBaseUrl = targetBaseUri.ToString();
        var keyFilePath = _frontendInstance?.ResolveBackendApiKeyPath(targetBaseUrl);

        if (string.IsNullOrWhiteSpace(keyFilePath) || !File.Exists(keyFilePath))
            throw new InvalidOperationException(
                $"API key file could not be resolved for backend '{targetBaseUrl}'. " +
                "Ensure the selected SharpClaw backend is running and has published discovery metadata.");

        var apiKey = File.ReadAllText(keyFilePath).Trim();
        lock (_targetLock)
        {
            if (_targetBaseUri == targetBaseUri)
            {
                _cachedApiKey = apiKey;
                _cachedApiKeyTarget = targetBaseUri;
            }
        }
        return apiKey;
    }

    /// <summary>
    /// The currently cached API key, or <c>null</c> if not yet resolved.
    /// Used to forward the verified key to child processes (e.g. gateway)
    /// without file I/O that may break under MSIX VFS virtualisation.
    /// </summary>
    public string? CachedApiKey
    {
        get
        {
            lock (_targetLock)
            {
                return _cachedApiKeyTarget == _targetBaseUri
                    ? _cachedApiKey
                    : null;
            }
        }
    }

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
                lock (_targetLock)
                {
                    _cachedApiKey = null;
                    _cachedApiKeyTarget = null;
                }
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(true);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002",
        Justification = "IDisposable must complete synchronously. Shared disposal runs on Task.Run without a UI SynchronizationContext and never invokes UI callbacks; async consumers use DisposeAsync.")]
    public void Dispose() => GetDisposalTaskAsync().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(GetDisposalTaskAsync());

    private Task GetDisposalTaskAsync()
    {
        lock (_disposeLock)
        {
            // Both disposal contracts join one receipt, including its failure.
            // The cleanup terminal owns only HTTP resources, so no UI turn is required.
            return _disposeTask ??= Task.Run(DisposeCoreAsync, CancellationToken.None);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _clientActions.RunCommandAsync(
                "client.api.dispose",
                async _ => await DisposeOwnedHttpAsync().ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A rejected/failed receipt cannot abandon an owned transport.
            // Join physical cleanup while retaining this original action fault.
            await CloseOwnedHttpAfterFailedReceiptAsync().ConfigureAwait(false);
            throw;
        }

        // A policy may settle successfully without invoking its terminal.
        // Disposal still promises physical release of the resources we own.
        await DisposeOwnedHttpAsync().ConfigureAwait(false);
    }

    private Task DisposeOwnedHttpAsync()
    {
        if (!_ownsHttp) return Task.CompletedTask;
        lock (_disposeLock)
        {
            // Separate physical ownership from the action receipt. Both paths
            // join this exact task, even if the action faults before settlement.
            return _ownedHttpDisposalTask ??= Task.Run(_http.Dispose, CancellationToken.None);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "A secondary resource-release failure is observed after a failed disposal receipt, while the original action fault remains the failure shared by all disposal callers.")]
    private async Task CloseOwnedHttpAfterFailedReceiptAsync()
    {
        try { await DisposeOwnedHttpAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
    }

    /// <summary>
    /// Emits bounded request metadata through the process logger. Bodies and
    /// credential-bearing headers are intentionally never collected.
    /// </summary>
    private sealed class HttpLoggingHandler(
        HttpMessageHandler inner,
        ILogger logger) : DelegatingHandler(inner)
    {
        private static readonly Action<ILogger, string, HttpMethod, string, long?, Exception?> LogStarted =
            LoggerMessage.Define<string, HttpMethod, string, long?>(LogLevel.Debug,
                new EventId(10, "HttpStarted"), "HTTP request {RequestId} started: {Method} {Path}; content length={ContentLength}");
        private static readonly Action<ILogger, string, long, HttpMethod, string, Exception?> LogFailed =
            LoggerMessage.Define<string, long, HttpMethod, string>(LogLevel.Error,
                new EventId(11, "HttpFailed"), "HTTP request {RequestId} failed after {ElapsedMilliseconds}ms: {Method} {Path}");
        private static readonly Action<ILogger, string, int, long, HttpMethod, string, long?, Exception?> LogCompleted =
            LoggerMessage.Define<string, int, long, HttpMethod, string, long?>(LogLevel.Information,
                new EventId(12, "HttpCompleted"), "HTTP request {RequestId} completed: {StatusCode} after {ElapsedMilliseconds}ms: {Method} {Path}; response length={ContentLength}");

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var path = SafePath(request.RequestUri);
            LogStarted(logger, id, request.Method, path, request.Content?.Headers.ContentLength, null);

            var sw = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogFailed(logger, id, sw.ElapsedMilliseconds, request.Method, path, ex);
                throw;
            }
            sw.Stop();

            LogCompleted(logger, id, (int)response.StatusCode, sw.ElapsedMilliseconds,
                request.Method, path, response.Content?.Headers.ContentLength, null);

            return response;
        }
    }

    private static string SafePath(Uri? uri)
    {
        if (uri is null)
            return string.Empty;
        return uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
    }

    private Uri GetTargetSnapshot()
    {
        lock (_targetLock)
            return _targetBaseUri;
    }

    private static Uri CreateTargetUri(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return new Uri(baseUrl, UriKind.Absolute);
    }

    private static Uri ResolveRequestUri(Uri targetBaseUri, string requestTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestTarget);

        if (HasAuthorityPrefix(requestTarget) ||
            HasUriScheme(requestTarget) ||
            !Uri.TryCreate(requestTarget, UriKind.Relative, out var relativeTarget) ||
            requestTarget.Contains('#', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The effective HTTP request target must contain only a local path and query.");
        }

        var resolved = new Uri(targetBaseUri, relativeTarget);
        if (!HasSameAuthority(targetBaseUri, resolved))
        {
            throw new InvalidOperationException(
                "The effective HTTP request target must preserve the selected Runtime authority.");
        }

        return resolved;
    }

    private static bool HasAuthorityPrefix(string requestTarget) =>
        requestTarget.Length >= 2 &&
        IsPathSeparator(requestTarget[0]) &&
        IsPathSeparator(requestTarget[1]);

    private static bool HasUriScheme(string requestTarget)
    {
        if (requestTarget.Length == 0 || !char.IsAsciiLetter(requestTarget[0]))
            return false;

        for (var index = 1; index < requestTarget.Length; index++)
        {
            var character = requestTarget[index];
            if (character == ':')
                return true;
            if (!char.IsAsciiLetterOrDigit(character) && character is not '+' and not '-' and not '.')
                return false;
        }

        return false;
    }

    private static bool IsPathSeparator(char value) => value is '/' or '\\';

    private static bool HasSameAuthority(Uri expected, Uri actual) =>
        string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        expected.Port == actual.Port &&
        string.Equals(expected.UserInfo, actual.UserInfo, StringComparison.Ordinal);
}
