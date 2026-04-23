using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using SharpClaw.Utils.Logging;

namespace SharpClaw.Services;

/// <summary>
/// Manages the lifecycle of the SharpClaw Gateway process.
/// <para>
/// Mirrors <see cref="BackendProcessManager"/> but targets
/// <c>SharpClaw.Gateway</c> in the <c>gateway</c> subfolder.
/// The gateway is optional — when <see cref="SkipLaunch"/> is
/// <c>true</c> (default), the client never attempts to start it.
/// </para>
/// </summary>
public sealed class GatewayProcessManager : IDisposable
{
    private readonly FrontendInstanceService? _frontendInstance;
    private readonly SessionLogWriter _sessionLogWriter;
    private Process? _process;
    private readonly string _executablePath;
    private string _backendBaseUrl;
    private string _gatewayUrl;
    private readonly List<string> _processOutput = [];
    private readonly object _outputLock = new();

    public const string DefaultGatewayUrl = "http://0.0.0.0:48924";

    /// <summary>
    /// <c>true</c> when we confirmed the gateway is reachable but was not
    /// started by this manager (i.e. an external/dev-time instance).
    /// </summary>
    public bool IsExternal { get; private set; }

    /// <summary>Current gateway base URL (bind address for the server).</summary>
    public string GatewayUrl => _gatewayUrl;

    /// <summary>Current backend base URL forwarded to the gateway.</summary>
    public string BackendBaseUrl => _backendBaseUrl;

    /// <summary>Full path to the bundled gateway executable.</summary>
    public string ExecutablePath => _executablePath;

    /// <summary>
    /// Client-connectable URL. Replaces the non-routable <c>0.0.0.0</c>
    /// bind address with <c>127.0.0.1</c> so HTTP calls from the Uno
    /// client actually reach the gateway.
    /// </summary>
    public string ClientUrl => _gatewayUrl.Replace("://0.0.0.0", "://127.0.0.1");

    public string? BundledGatewayInstanceRoot => _frontendInstance is null
        ? null
        : Path.Combine(_frontendInstance.Paths.InstanceRoot, "stack", "gateway");

    /// <summary>
    /// When <c>true</c>, <see cref="EnsureStartedAsync"/> will never launch
    /// the bundled gateway process — it only probes for an external instance.
    /// Defaults to <c>true</c> (gateway is opt-in).
    /// </summary>
    public bool SkipLaunch { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, the process is left running when the frontend
    /// shuts down instead of being killed. The next frontend launch will
    /// detect it via port/process probes and attach as external.
    /// </summary>
    public bool Persistent { get; set; }

    /// <summary>
    /// Pre-verified API key to forward to the gateway process via the
    /// <c>InternalApi__ApiKey</c> environment variable. When set, the
    /// gateway receives the exact key the Uno client already authenticated
    /// with — bypassing any file I/O that may break under MSIX VFS
    /// virtualisation.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gateway service token that proves the gateway's identity to the core
    /// API beyond the shared API key. Read from the <c>.gateway-token</c>
    /// file written by the core API's <c>ApiKeyProvider</c>.
    /// </summary>
    public string? GatewayToken { get; set; }

    /// <summary>
    /// All stdout + stderr lines captured from the bundled process
    /// (most recent last). Thread-safe snapshot.
    /// </summary>
    public IReadOnlyList<string> ProcessOutput
    {
        get { lock (_outputLock) return [.. _processOutput]; }
    }

    /// <summary>Exit code of the bundled process, or <c>null</c> if still running or never started.</summary>
    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    /// <summary>Clears all captured process output.</summary>
    public void ClearOutput() { lock (_outputLock) _processOutput.Clear(); }

    /// <summary>Returns the number of captured output lines without copying.</summary>
    public int OutputLineCount { get { lock (_outputLock) return _processOutput.Count; } }

    public GatewayProcessManager(
        string gatewayUrl,
        string backendBaseUrl,
        SessionLogWriter sessionLogWriter,
        FrontendInstanceService? frontendInstance = null)
    {
        _frontendInstance = frontendInstance;
        _sessionLogWriter = sessionLogWriter;
        _backendBaseUrl = backendBaseUrl;
        _gatewayUrl = gatewayUrl;

        var baseDir = AppContext.BaseDirectory;
        var exeName = OperatingSystem.IsWindows()
            ? "SharpClaw.Gateway.exe"
            : "SharpClaw.Gateway";

        _executablePath = Path.Combine(baseDir, "gateway", exeName);
    }

    /// <summary>
    /// Changes the target gateway URL. Only meaningful before the next
    /// <see cref="EnsureStartedAsync"/> call.
    /// </summary>
    public void UpdateGatewayUrl(string gatewayUrl) => _gatewayUrl = gatewayUrl;

    /// <summary>
    /// Changes the target backend URL the gateway should forward to.
    /// </summary>
    public void UpdateBackendBaseUrl(string backendBaseUrl) => _backendBaseUrl = backendBaseUrl;

    /// <summary>
    /// Returns <c>true</c> if the bundled gateway executable exists on disk.
    /// </summary>
    public bool IsAvailable => File.Exists(_executablePath);

    /// <summary>
    /// Returns <c>true</c> if the gateway process was started by this
    /// manager and is still running.
    /// </summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Probes the gateway's root to check reachability.
    /// </summary>
    public async Task<bool> IsGatewayReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"{ClientUrl}/healthz", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures the gateway is available. Checks in order:
    /// <list type="number">
    ///   <item>Is the bundled process already running? → done.</item>
    ///   <item>Is another gateway process listening on the port? → mark external.</item>
    ///   <item>Is the gateway already reachable? → mark external.</item>
    ///   <item>Is the bundled gateway available? → launch it.</item>
    ///   <item>Neither → throw.</item>
    /// </list>
    /// </summary>
    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        if (IsRunning)
            return;

        if (IsGatewayProcessOnPort())
        {
            IsExternal = true;
            return;
        }

        if (await IsGatewayReachableAsync(ct))
        {
            IsExternal = true;
            return;
        }

        if (SkipLaunch)
        {
            IsExternal = true;
            throw new InvalidOperationException(
                $"Gateway launch is disabled (Gateway:Enabled = false). " +
                $"Ensure the gateway is running at {_gatewayUrl}.");
        }

        if (!IsAvailable)
            throw new InvalidOperationException(
                "SharpClaw Gateway is not reachable and no bundled gateway was found. " +
                "Start the gateway project manually or publish with /p:BundleGateway=true.");

        Start();
    }

    private bool IsGatewayProcessOnPort()
    {
        if (!TryGetPortFromUrl(out var targetPort))
            return false;

        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            bool portInUse = false;
            foreach (var ep in listeners)
            {
                if (ep.Port == targetPort)
                {
                    portInUse = true;
                    break;
                }
            }

            if (!portInUse)
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            var candidates = Process.GetProcessesByName("SharpClaw.Gateway");
            return candidates.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetPortFromUrl(out int port)
    {
        port = 0;
        try
        {
            var uri = new Uri(_gatewayUrl);
            port = uri.Port;
            return port > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the bundled gateway process.
    /// </summary>
    public void Start()
    {
        if (IsRunning)
            return;

        if (!IsAvailable)
            throw new FileNotFoundException(
                $"SharpClaw Gateway not found at '{_executablePath}'. " +
                "Ensure the gateway is published into the 'gateway' subfolder.");

        lock (_outputLock) _processOutput.Add($"[{DateTime.Now:HH:mm:ss}] ── Starting gateway process ──");
        _sessionLogWriter.AppendDebug("[gateway] Starting bundled gateway process.");
        IsExternal = false;

        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
        };

        // Bind to the configured URL.
        psi.EnvironmentVariables["ASPNETCORE_URLS"] = _gatewayUrl;
        psi.EnvironmentVariables["InternalApi__BaseUrl"] = _backendBaseUrl;
        psi.ArgumentList.Add($"--InternalApi:BaseUrl={_backendBaseUrl}");

        if (!string.IsNullOrWhiteSpace(BundledGatewayInstanceRoot))
        {
            psi.EnvironmentVariables["SHARPCLAW_INSTANCE_ROOT"] = BundledGatewayInstanceRoot;
            psi.EnvironmentVariables["SHARPCLAW_DATA_DIR"] = Path.Combine(BundledGatewayInstanceRoot, "Data");
        }

        if (_frontendInstance is not null)
            psi.EnvironmentVariables["SHARPCLAW_SHARED_ROOT"] = _frontendInstance.Paths.SharedRoot;

        if (_frontendInstance is not null)
        {
            var manifest = _frontendInstance.Paths.Manifest;
            psi.EnvironmentVariables["SharpClawInstance__SelectedBackendBaseUrl"] = _backendBaseUrl;
            psi.ArgumentList.Add($"--SharpClawInstance:SelectedBackendBaseUrl={_backendBaseUrl}");

            if (!string.IsNullOrWhiteSpace(manifest.SelectedBackendInstanceId))
            {
                psi.EnvironmentVariables["SharpClawInstance__SelectedBackendInstanceId"] = manifest.SelectedBackendInstanceId;
                psi.ArgumentList.Add($"--SharpClawInstance:SelectedBackendInstanceId={manifest.SelectedBackendInstanceId}");
            }

            if (!string.IsNullOrWhiteSpace(manifest.SelectedBackendBindingKind))
            {
                psi.EnvironmentVariables["SharpClawInstance__SelectedBackendBindingKind"] = manifest.SelectedBackendBindingKind;
                psi.ArgumentList.Add($"--SharpClawInstance:SelectedBackendBindingKind={manifest.SelectedBackendBindingKind}");
            }
        }

        // Pass the internal API key directly to the gateway process so it
        // does not need to locate the key file itself.  In MSIX packaging,
        // the Uno host process and its child processes may resolve
        // %LOCALAPPDATA% to different paths due to VFS virtualisation,
        // causing the gateway to read a stale or missing key and get 401
        // on every request to the core API.
        //
        // Priority: in-memory ApiKey (set by BootModel/SettingsPage from
        // the verified SharpClawApiClient cache) → file fallback.
        // .NET configuration binds InternalApi__ApiKey → InternalApi:ApiKey.
        string? resolvedKey = ApiKey;

        if (string.IsNullOrEmpty(resolvedKey))
        {
            try
            {
                var keyFilePath = _frontendInstance?.ResolveBackendApiKeyPath(_backendBaseUrl);

                if (!string.IsNullOrWhiteSpace(keyFilePath) && File.Exists(keyFilePath))
                    resolvedKey = File.ReadAllText(keyFilePath).Trim();
            }
            catch
            {
                // Best-effort — the gateway will fall back to reading the file itself.
            }
        }

        if (!string.IsNullOrEmpty(resolvedKey))
        {
            // Set via BOTH env var and command-line argument for maximum
            // robustness. Command-line args are the highest-priority
            // configuration source in WebApplication.CreateBuilder(args)
            // — nothing can override them.
            psi.EnvironmentVariables["InternalApi__ApiKey"] = resolvedKey;
            psi.ArgumentList.Add($"--InternalApi:ApiKey={resolvedKey}");
            lock (_outputLock) _processOutput.Add($"[{DateTime.Now:HH:mm:ss}] API key forwarded to gateway via CLI arg ({(ApiKey is not null ? "in-memory" : "file")}, {resolvedKey.Length} chars, prefix={resolvedKey[..Math.Min(6, resolvedKey.Length)]}..).");
            _sessionLogWriter.AppendDebug($"[gateway] API key forwarded to bundled gateway ({(ApiKey is not null ? "in-memory" : "file")}).");
        }
        else
        {
            lock (_outputLock) _processOutput.Add($"[{DateTime.Now:HH:mm:ss}] ⚠ No API key available to forward — gateway may get 401.");
            _sessionLogWriter.AppendDebug("[gateway] No API key available to forward to bundled gateway.");
        }

        // ── Gateway service token ────────────────────────────────────
        string? resolvedGatewayToken = GatewayToken;

        if (string.IsNullOrEmpty(resolvedGatewayToken))
        {
            try
            {
                var tokenFilePath = _frontendInstance?.ResolveBackendGatewayTokenPath(_backendBaseUrl);

                if (!string.IsNullOrWhiteSpace(tokenFilePath) && File.Exists(tokenFilePath))
                    resolvedGatewayToken = File.ReadAllText(tokenFilePath).Trim();
            }
            catch { /* best-effort — gateway falls back to file read itself */ }
        }

        if (!string.IsNullOrEmpty(resolvedGatewayToken))
        {
            psi.EnvironmentVariables["InternalApi__GatewayToken"] = resolvedGatewayToken;
            psi.ArgumentList.Add($"--InternalApi:GatewayToken={resolvedGatewayToken}");
            lock (_outputLock) _processOutput.Add($"[{DateTime.Now:HH:mm:ss}] Gateway token forwarded ({resolvedGatewayToken.Length} chars).");
            _sessionLogWriter.AppendDebug("[gateway] Gateway token forwarded to bundled gateway.");
        }

        _process = Process.Start(psi);

        _process!.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_outputLock) _processOutput.Add($"[{DateTime.Now:HH:mm:ss}] {e.Data}");
                _sessionLogWriter.AppendDebug($"[gateway] {e.Data}");
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_outputLock) _processOutput.Add($"[{DateTime.Now:HH:mm:ss}] [stderr] {e.Data}");
                _sessionLogWriter.AppendException($"[gateway stderr] {e.Data}");
            }
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>
    /// Gracefully stops the gateway process. No-op if it was
    /// started externally.
    /// </summary>
    public void Stop()
    {
        if (IsExternal || _process is null or { HasExited: true })
            return;

        try
        {
            if (!_process.CloseMainWindow())
                _process.Kill(entireProcessTree: true);

            _process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Releases the process handle without killing the child process.
    /// The gateway continues running as an orphaned background process
    /// and will be detected via port/process probes on the next launch.
    /// </summary>
    public void Detach()
    {
        if (_process is null or { HasExited: true })
            return;

        try
        {
            _process.CancelOutputRead();
            _process.CancelErrorRead();
        }
        catch { /* best-effort */ }

        _process.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        if (Persistent && _process is { HasExited: false })
            Detach();
        else
            Stop();

        _process?.Dispose();
    }
}
