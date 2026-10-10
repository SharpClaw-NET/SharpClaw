using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpClaw.Configuration;
using SharpClaw.Core.Kernel;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Services;

/// <summary>Configures the owned local Runtime to proxy one explicitly bound remote Gateway.</summary>
internal sealed class RemoteBackendConnectionService : IAsyncDisposable
{
    private const int MaximumMetadataBytes = 8192;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private readonly FrontendInstanceService _frontend;
    private readonly BackendProcessManager _backend;
    private readonly GatewayProcessManager _gateway;
    private readonly SharpClawApiClient _api;
    private readonly ClientActionDispatcher _actions;
    private readonly ModulePackageStore _modules;
    private readonly Func<HttpMessageHandler> _createPeerHandler;
    private readonly Lock _changeLock = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "DisposeAsync joins one cached context-free shutdown task. DisposeCoreAsync cancels this source, joins the retained active change and its physical terminal, then disposes this source in finally. The analyzer cannot trace the Task.Run/ValueTask disposal path; early disposal would violate active-operation ownership.")]
    private readonly CancellationTokenSource _lifetime;
    private Task<RemoteBackendConnectionStatus>? _activeChange;
    private Task? _shutdownTask;

    public RemoteBackendConnectionService(
        FrontendInstanceService frontend,
        BackendProcessManager backend,
        GatewayProcessManager gateway,
        SharpClawApiClient api,
        ClientActionDispatcher actions,
        ModulePackageStore modules)
        : this(frontend, backend, gateway, api, actions, modules,
            static () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, CheckCertificateRevocationList = true })
    {
    }

    internal RemoteBackendConnectionService(
        FrontendInstanceService frontend,
        BackendProcessManager backend,
        GatewayProcessManager gateway,
        SharpClawApiClient api,
        ClientActionDispatcher actions,
        ModulePackageStore modules,
        Func<HttpMessageHandler> createPeerHandler)
    {
        _frontend = frontend ?? throw new ArgumentNullException(nameof(frontend));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _createPeerHandler = createPeerHandler ?? throw new ArgumentNullException(nameof(createPeerHandler));
        _lifetime = new CancellationTokenSource();
    }

    public bool IsProxyConfigured() => IsProxyConfigured(_frontend);

    internal static bool IsProxyConfigured(FrontendInstanceService frontend) => ReadConnection(frontend) is not null;

    public RemoteBackendConnectionStatus GetConfiguredStatus()
    {
        var connection = ReadConnection(_frontend);
        return new(connection is not null, false, connection?.GatewayBaseUri, false);
    }

    public bool HasEnabledLocalModules() => _modules.ReadInstalled()
        .Concat(ModulePackageStore.ReadIdentities(
            Path.Combine(Path.GetDirectoryName(_backend.ExecutablePath)!, "contributions"), true))
        .Any(module => BundledModuleSetup.IsEnabled(_frontend, module.Id, module.DefaultEnabled));

    public Task<RemoteBackendConnectionStatus> ConnectAsync(
        Uri gatewayAddress, string? accessToken, CancellationToken cancellationToken)
    {
        var connection = RemoteGatewayConnection.Create(gatewayAddress, accessToken);
        RequireOwnedTarget();
        RejectLocalProxyLoop(connection);
        return RunChangeAsync("client.remote.connect",
            token => ConnectCoreAsync(connection, token), cancellationToken);
    }

    public Task<RemoteBackendConnectionStatus> DisconnectAsync(CancellationToken cancellationToken)
    {
        RequireOwnedTarget();
        return RunChangeAsync("client.remote.disconnect", DisconnectCoreAsync, cancellationToken);
    }

    public async Task<RemoteBackendConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = ReadConnection(_frontend);
        cancellationToken.ThrowIfCancellationRequested();
        if (connection is null) return new(false, false, null, false);
        if (!_backend.OwnsCurrentTarget || !IsSelectedLocalTarget())
            return new(true, false, connection.GatewayBaseUri, true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MetadataTimeout);
        try
        {
            var matches = false;
            await _api.ConsumeStreamAsync("GET", "/remote/status", null, async (response, token) =>
            {
                if (!response.IsSuccessStatusCode) return;
                var bytes = await ReadBoundedMetadataAsync(response, token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
                matches = MatchesProxyStatus(document.RootElement, connection.GatewayBaseUri);
            }, deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!matches)
                return new(true, false, connection.GatewayBaseUri, true);
            using var readiness = await _api.GetAsync("/readyz", deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, readiness.IsSuccessStatusCode, connection.GatewayBaseUri, !readiness.IsSuccessStatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or
            InvalidOperationException or OperationCanceledException or KernelActionFailedException or KernelActionCancelledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, false, connection.GatewayBaseUri, true);
        }
    }

    private async Task<RemoteBackendConnectionStatus> ConnectCoreAsync(
        RemoteGatewayConnection connection, CancellationToken cancellationToken)
    {
        using var targetGuard = _api.HoldTarget(new Uri(_backend.ApiUrl));
        await ProbePeerAsync(connection, cancellationToken).ConfigureAwait(false);
        RequireOwnedTarget();
        cancellationToken.ThrowIfCancellationRequested();
        await WriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        // The protected document is now authoritative. Page/action cancellation
        // cannot abandon process settlement or silently restore local mode.
        using var settlement = new CancellationTokenSource(StartupTimeout);
        await StopOwnedProcessesAsync(settlement.Token).ConfigureAwait(false);
        await StartOwnedProcessesAsync(settlement.Token).ConfigureAwait(false);
        return await GetStatusAsync(settlement.Token).ConfigureAwait(false);
    }

    private async Task<RemoteBackendConnectionStatus> DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        using var targetGuard = _api.HoldTarget(new Uri(_backend.ApiUrl));
        RequireOwnedTarget();
        // Check local activation before changing the protected remote binding.
        var startLocal = HasEnabledLocalModules();
        cancellationToken.ThrowIfCancellationRequested();
        await WriteConnectionAsync(null, cancellationToken).ConfigureAwait(false);
        using var settlement = new CancellationTokenSource(StartupTimeout);
        await StopOwnedProcessesAsync(settlement.Token).ConfigureAwait(false);
        if (startLocal) await StartOwnedProcessesAsync(settlement.Token).ConfigureAwait(false);
        return new(false, false, null, false);
    }

    private Task<RemoteBackendConnectionStatus> RunChangeAsync(
        string operation,
        Func<CancellationToken, Task<RemoteBackendConnectionStatus>> change,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_changeLock)
        {
            ObjectDisposedException.ThrowIf(_shutdownTask is not null, this);
            if (_activeChange is { IsCompleted: false })
                throw new InvalidOperationException("A remote connection change is already in progress.");
            // Publish ownership before the context-free task can execute. App
            // shutdown closes admission under this same lock and joins it.
            return _activeChange = Task.Run(() => RunAdmittedChangeAsync(
                operation, change, cancellationToken), CancellationToken.None);
        }
    }

    private async Task<RemoteBackendConnectionStatus> RunAdmittedChangeAsync(
        string operation,
        Func<CancellationToken, Task<RemoteBackendConnectionStatus>> change,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var admission = new Lock();
        var accepting = true;
        Task<RemoteBackendConnectionStatus>? work = null;
        try
        {
            var result = await _actions.RunCommandAsync(operation, token =>
            {
                lock (admission)
                {
                    if (!accepting || work is not null)
                        throw new InvalidOperationException("This remote connection change may run only once.");
                    // This terminal owns configuration/process work, never UI.
                    // Retain its task even if the action receipt settles early.
                    work = Task.Run(() => change(token), CancellationToken.None);
                    return new ValueTask<RemoteBackendConnectionStatus>(work);
                }
            }, lifetime.Token).ConfigureAwait(false);
            lock (admission)
            {
                accepting = false;
                if (work is null) throw new InvalidOperationException("The remote connection change was not accepted.");
            }
            return result;
        }
        finally
        {
            Task<RemoteBackendConnectionStatus>? ownedWork;
            lock (admission)
            {
                accepting = false;
                ownedWork = work;
            }
            if (ownedWork is not null) await ObserveSettlementAsync(ownedWork).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_changeLock)
        {
            // All callers join one shutdown receipt. No UI synchronization
            // context is needed to cancel probes or settle owned processes.
            var shutdown = _shutdownTask ??= Task.Run(DisposeCoreAsync, CancellationToken.None);
            GC.SuppressFinalize(this);
            return new ValueTask(shutdown);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Shutdown observes a cancellation-callback fault using bounded metadata, then still joins the admitted change and disposes its cancellation source. Cancellation failures cannot abandon a committed restart.")]
    private async Task DisposeCoreAsync()
    {
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
        finally
        {
            Task<RemoteBackendConnectionStatus>? active;
            lock (_changeLock) active = _activeChange;
            try { if (active is not null) await ObserveSettlementAsync(active).ConfigureAwait(false); }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "The admitted physical terminal is joined after action settlement. Its secondary failure is observed using bounded exception metadata while the original receipt failure is retained for the caller.")]
    private static async Task ObserveSettlementAsync(Task<RemoteBackendConnectionStatus> work)
    {
        try
        {
            // Both call sites supply a retained Task.Run owned by this service.
            // The terminal has no UI dependency and must be joined before its
            // process/HTTP owners are disposed, even after receipt cancellation.
#pragma warning disable VSTHRD003
            await work.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
    }

    private async Task ProbePeerAsync(RemoteGatewayConnection connection, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MetadataTimeout);
        using var http = CreatePeerHttpClient();
        foreach (var metadata in new[] { "health", "ping", "gateway/status" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.GatewayBaseUri, metadata));
            if (connection.AccessToken is { } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("The remote Gateway did not accept the connection probe.",
                    inner: null, response.StatusCode);
            var bytes = await ReadBoundedMetadataAsync(response, deadline.Token).ConfigureAwait(false);
            if (string.Equals(metadata, "ping", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (!MatchesGatewayMetadata(document.RootElement, metadata))
                throw new InvalidDataException("The remote server did not provide compatible SharpClaw Gateway metadata.");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool MatchesProxyStatus(JsonElement root, Uri gatewayAddress) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String &&
        string.Equals(mode.GetString(), "gateway-proxy", StringComparison.Ordinal) &&
        root.TryGetProperty("gatewayUrl", out var address) && address.ValueKind == JsonValueKind.String &&
        Uri.TryCreate(address.GetString(), UriKind.Absolute, out var activeAddress) && activeAddress == gatewayAddress &&
        root.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.True;

    private static bool MatchesGatewayMetadata(JsonElement root, string metadata)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String) return false;
        if (string.Equals(metadata, "health", StringComparison.Ordinal))
            return string.Equals(status.GetString(), "healthy", StringComparison.Ordinal);
        return string.Equals(status.GetString(), "ready", StringComparison.Ordinal) &&
            root.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(runtime.GetString(), UriKind.Absolute, out var runtimeAddress) &&
            (string.Equals(runtimeAddress.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
             string.Equals(runtimeAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) &&
            string.IsNullOrEmpty(runtimeAddress.UserInfo);
    }

    private static async Task<byte[]> ReadBoundedMetadataAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumMetadataBytes)
            throw new InvalidDataException("Gateway metadata exceeds its size limit.");
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var streamDisposal = stream.ConfigureAwait(false);
        using var bounded = new MemoryStream();
        await ModulePackageSources.CopyBoundedAsync(stream, bounded, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        return bounded.ToArray();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "The returned HttpClient owns the factory-acquired handler through disposeHandler:true. Constructor/configuration failures dispose either that client or the still-owned handler; every probe disposes the returned client.")]
    private HttpClient CreatePeerHttpClient()
    {
        var handler = _createPeerHandler();
        HttpClient? http = null;
        try
        {
            http = new HttpClient(handler, disposeHandler: true);
            http.Timeout = MetadataTimeout;
            return http;
        }
        catch
        {
            if (http is not null) http.Dispose();
            else handler.Dispose();
            throw;
        }
    }

    private async Task StopOwnedProcessesAsync(CancellationToken cancellationToken)
    {
        RequireOwnedTarget();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_gateway.IsExternal)
        {
            _gateway.Stop();
            if (_gateway.IsRunning) throw new InvalidOperationException("The owned local Gateway did not stop.");
        }
        _backend.Stop();
        if (_backend.IsRunning) throw new InvalidOperationException("The owned local Runtime did not stop.");
        await _api.InvalidateApiKeyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartOwnedProcessesAsync(CancellationToken cancellationToken)
    {
        await _backend.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (!_backend.OwnsCurrentTarget)
            throw new InvalidOperationException("The local Runtime target is no longer owned by this frontend.");
        await _api.WaitForReadyAsync(StartupTimeout, cancellationToken).ConfigureAwait(false);
        if (!_gateway.SkipLaunch && !_gateway.IsExternal && _gateway.IsAvailable)
        {
            _gateway.ApiKey = _api.CachedApiKey;
            _gateway.GatewayToken = null;
            await _gateway.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void RequireOwnedTarget()
    {
        BundledModuleSetup.RequireOwnedTarget(_backend);
        if (!IsSelectedLocalTarget())
            throw new InvalidOperationException("Select this frontend's bundled local Runtime before configuring a remote connection.");
    }

    private bool IsSelectedLocalTarget() => Uri.TryCreate(_backend.ApiUrl, UriKind.Absolute, out var backendAddress) &&
        Uri.TryCreate(_api.BaseUrl, UriKind.Absolute, out var selectedAddress) && backendAddress == selectedAddress;

    private void RejectLocalProxyLoop(RemoteGatewayConnection connection)
    {
        var peer = connection.GatewayBaseUri;
        foreach (var target in new[] { _backend.ApiUrl, _gateway.ClientUrl })
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var local) &&
                string.Equals(peer.Scheme, local.Scheme, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(peer.IdnHost, local.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                 (peer.IsLoopback && local.IsLoopback)) && peer.Port == local.Port)
                throw new InvalidOperationException("The remote Gateway must be different from this frontend's local Runtime and Gateway.");
        }
    }

    private async Task WriteConnectionAsync(RemoteGatewayConnection? connection, CancellationToken cancellationToken)
    {
        var paths = BackendPaths(_frontend);
        var store = new SupprocomSecretFileStore(LocalEnvironment.CreateSecretsOptions(
            paths.ConfigDirectory, isDevelopment: false, paths));
        await store.UpdateDocumentAsync(settings => UpdateSettings(settings, connection), cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<SupprocomSecretSetting> UpdateSettings(
        IReadOnlyList<SupprocomSecretSetting> settings, RemoteGatewayConnection? connection)
    {
        var values = settings.ToDictionary(setting => setting.Key, setting => setting.Value,
            StringComparer.OrdinalIgnoreCase);
        values["RemoteBackend:Enabled"] = connection is null ? "false" : "true";
        values.Remove("RemoteBackend:GatewayUrl");
        values.Remove("RemoteBackend:AccessToken");
        if (connection is not null)
        {
            values["RemoteBackend:GatewayUrl"] = connection.GatewayBaseUri.AbsoluteUri;
            if (connection.AccessToken is { } token) values["RemoteBackend:AccessToken"] = token;
        }
        return values.Select(pair => new SupprocomSecretSetting(pair.Key, pair.Value)).ToArray();
    }

    private static RemoteGatewayConnection? ReadConnection(FrontendInstanceService frontend)
    {
        var paths = BackendPaths(frontend);
        if (!File.Exists(Path.Combine(paths.ConfigDirectory, ".env")))
            return null;
        var configuration = new ConfigurationBuilder().AddSupprocomSecrets(
            LocalEnvironment.CreateSecretsOptions(paths.ConfigDirectory, false, paths)).Build();
        try { return RemoteGatewayConnection.FromConfiguration(configuration); }
        finally { (configuration as IDisposable)?.Dispose(); }
    }

    private static SharpClawInstancePaths BackendPaths(FrontendInstanceService frontend) =>
        new(SharpClawInstanceKind.Backend, frontend.BundledBackendInstanceRoot, frontend.Paths.SharedRoot);
}
