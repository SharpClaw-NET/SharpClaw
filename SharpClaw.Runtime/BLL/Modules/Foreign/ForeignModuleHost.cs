using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Shared.Logging;

namespace SharpClaw.Runtime.BLL.Modules.Foreign;

internal sealed class ForeignModuleHost : IForeignModuleRuntimeHost
{
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EarlyExitObservationGrace = TimeSpan.FromMilliseconds(250);

    private readonly ModuleManifest _manifest;
    private readonly ModuleManifestRuntimeInfo _runtimeInfo;
    private readonly ForeignModuleHostLaunchOptions _options;
    private readonly Process _process;
    private readonly HttpClient _httpClient;
    private ServiceProvider _serviceProvider;
    private readonly ForeignModuleHostCapabilityServer? _capabilityServer;
    private readonly ForeignModuleProxy _moduleProxy;
    private readonly BoundedOutputTail _stdout = new();
    private readonly BoundedOutputTail _stderr = new();
    private readonly object _stdoutLock = new();
    private readonly object _stderrLock = new();
    private readonly ILogger? _logger;
    private readonly SharpClawModuleLogContext? _ownership;
    private readonly TaskCompletionSource<int> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _drainTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _inFlightCount;
    private int _shutdownStarted;
    private int _disposed;
    private volatile bool _draining;
    private volatile bool _startupCompleted;

    private ForeignModuleHost(
        ModuleManifest manifest,
        ModuleManifestRuntimeInfo runtimeInfo,
        ForeignModuleHostLaunchOptions options,
        Process process,
        HttpClient httpClient,
        ForeignModuleProtocolClient client,
        ServiceProvider serviceProvider,
        ForeignModuleHostCapabilityServer? capabilityServer)
    {
        _manifest = manifest;
        _runtimeInfo = runtimeInfo;
        _options = options;
        _process = process;
        _httpClient = httpClient;
        ProtocolClient = client;
        _serviceProvider = serviceProvider;
        _capabilityServer = capabilityServer;
        SourceDirectory = options.ModuleDirectory;
        _moduleProxy = new ForeignModuleProxy(manifest, client, () => ShutdownSidecarAsync(CancellationToken.None));
        Module = _moduleProxy;
        _logger = options.HostServices?.GetService<ILoggerFactory>()?.CreateLogger("SharpClaw.ModuleHost");
        var runtime = options.HostServices?.GetService<SharpClawLogRuntime>();
        _ownership = runtime is null
            ? null
            : new SharpClawModuleLogContext(
                manifest.Id,
                TryResolveFileVersion(options.ExecutablePath),
                SharpClawModuleHostKind.RuntimeSidecar,
                runtime.BootId);
    }

    public ISharpClawCoreModule Module { get; }
    public string SourceDirectory { get; }
    public IServiceProvider Services => _serviceProvider;
    public ForeignModuleProtocolClient ProtocolClient { get; }
    public ForeignModuleHandshakeResponse Handshake { get; private set; } = null!;
    public IReadOnlyList<ForeignModuleEndpointDescriptor> Endpoints { get; private set; } = [];
    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;
    public ForeignModuleProcessOutput CapturedOutput => SnapshotOutput();

    public static async Task<ForeignModuleHost> StartAsync(
        ModuleManifest manifest,
        ModuleManifestRuntimeInfo runtimeInfo,
        ForeignModuleHostLaunchOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(runtimeInfo);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (runtimeInfo.IsDotNet && !runtimeInfo.IsSidecarHostMode)
            throw new ArgumentException(
                $"Foreign module host cannot start dotnet module '{manifest.Id}' unless hostMode is '{ModuleManifestRuntimeInfo.HostModeSidecar}'.",
                nameof(runtimeInfo));

        if (!Directory.Exists(options.ModuleDirectory))
            throw new DirectoryNotFoundException(
                $"Foreign module directory not found: '{options.ModuleDirectory}'.");

        Directory.CreateDirectory(options.ModuleDataDirectory);

        ForeignModuleHost? host = null;
        ForeignModuleHostCapabilityServer? capabilityServer = null;
        try
        {
            capabilityServer = options.HostServices is null
                ? null
                : ForeignModuleHostCapabilityServer.Start(manifest.Id, options.HostServices);

            var process = CreateProcess(manifest, runtimeInfo, options, capabilityServer);
            var httpClient = new HttpClient
            {
                BaseAddress = options.ControlAddress,
                Timeout = Timeout.InfiniteTimeSpan,
            };
            var client = new ForeignModuleProtocolClient(httpClient, options.ControlToken);
            var services = new ServiceCollection().BuildServiceProvider();
            host = new ForeignModuleHost(
                manifest,
                runtimeInfo,
                options,
                process,
                httpClient,
                client,
                services,
                capabilityServer);

            host.AttachOutputReaders();

            if (!process.Start())
            {
                throw new ForeignModuleStartupException(
                    $"Foreign module '{manifest.Id}' process did not start.",
                    host.SnapshotOutput());
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await host.WaitForReadinessAsync(ct);
            return host;
        }
        catch
        {
            if (host is not null)
                await host.DisposeAfterFailedStartupAsync();
            else if (capabilityServer is not null)
                await capabilityServer.DisposeAsync();

            throw;
        }
    }

    public IServiceScope CreateScope() => _serviceProvider.CreateScope();

    public Task<HttpResponseMessage> SendEndpointRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct = default)
    {
        request.Headers.Remove(ForeignModuleProtocol.TokenHeaderName);
        request.Headers.TryAddWithoutValidation(
            ForeignModuleProtocol.TokenHeaderName,
            _options.ControlToken);
        return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task<ClientWebSocket> ConnectEndpointWebSocketAsync(
        string pathAndQuery,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        CancellationToken ct = default)
    {
        var target = new Uri(_options.ControlAddress, pathAndQuery);
        var uri = new UriBuilder(target)
        {
            Scheme = string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws",
        }.Uri;

        var socket = new ClientWebSocket();
        try
        {
            foreach (var (name, values) in headers)
            {
                if (ShouldSkipWebSocketForwardedHeader(name))
                    continue;

                socket.Options.SetRequestHeader(name, string.Join(",", values));
            }

            socket.Options.SetRequestHeader(ForeignModuleProtocol.TokenHeaderName, _options.ControlToken);
            await socket.ConnectAsync(uri, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public bool TryAcquireExecution()
    {
        while (true)
        {
            if (_draining || _process.HasExited) return false;

            var current = Volatile.Read(ref _inFlightCount);
            if (Interlocked.CompareExchange(ref _inFlightCount, current + 1, current) == current)
            {
                if (_draining || _process.HasExited)
                {
                    ReleaseExecution();
                    return false;
                }

                return true;
            }
        }
    }

    public void ReleaseExecution()
    {
        var remaining = Interlocked.Decrement(ref _inFlightCount);
        if (remaining == 0 && _draining)
            _drainTcs.TrySetResult();
    }

    public async Task DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        _draining = true;
        Thread.MemoryBarrier();

        if (Volatile.Read(ref _inFlightCount) == 0)
        {
            _drainTcs.TrySetResult();
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await _drainTcs.Task.WaitAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await ShutdownSidecarAsync(CancellationToken.None);
        await _serviceProvider.DisposeAsync();
        if (_capabilityServer is not null)
            await _capabilityServer.DisposeAsync();

        _httpClient.Dispose();
        _process.Dispose();
    }

    private static Process CreateProcess(
        ModuleManifest manifest,
        ModuleManifestRuntimeInfo runtimeInfo,
        ForeignModuleHostLaunchOptions options,
        ForeignModuleHostCapabilityServer? capabilityServer)
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = options.WorkingDirectory ?? options.ModuleDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in options.Arguments)
            psi.ArgumentList.Add(argument);

        foreach (var (key, value) in options.Environment)
            psi.Environment[key] = value;

        psi.Environment[ForeignModuleProtocol.ModuleDirectoryEnv] = options.ModuleDirectory;
        psi.Environment[ForeignModuleProtocol.ModuleDataDirectoryEnv] = options.ModuleDataDirectory;
        psi.Environment[ForeignModuleProtocol.ControlAddressEnv] = options.ControlAddress.ToString();
        psi.Environment[ForeignModuleProtocol.ControlTokenEnv] = options.ControlToken;
        psi.Environment[ForeignModuleProtocol.ModuleIdEnv] = manifest.Id;
        psi.Environment[ForeignModuleProtocol.ModuleRuntimeEnv] = runtimeInfo.Runtime;
        if (capabilityServer is not null)
        {
            psi.Environment[ForeignModuleHostCapabilityProtocol.AddressEnv] =
                capabilityServer.Address.ToString();
            psi.Environment[ForeignModuleHostCapabilityProtocol.TokenEnv] =
                capabilityServer.Token;
        }

        return new Process { StartInfo = psi, EnableRaisingEvents = true };
    }

    private static bool ShouldSkipWebSocketForwardedHeader(string name) =>
        string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Sec-WebSocket-Version", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Sec-WebSocket-Extensions", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, ForeignModuleProtocol.TokenHeaderName, StringComparison.OrdinalIgnoreCase);

    private void AttachOutputReaders()
    {
        _process.OutputDataReceived += (_, e) => CaptureLine(_stdout, _stdoutLock, e.Data, isError: false);
        _process.ErrorDataReceived += (_, e) => CaptureLine(_stderr, _stderrLock, e.Data, isError: true);
        _process.Exited += (_, _) =>
        {
            var exitCode = -1;
            try { exitCode = _process.ExitCode; }
            catch { }

            if (exitCode != 0 && _logger is not null)
            {
                using var ownership = _ownership is null
                    ? null
                    : SharpClawLogOwnership.Push(_ownership);
                _logger.LogError(
                    "Sidecar module exited with code {ExitCode}; stdout tail={StdoutTail}; stderr tail={StderrTail}",
                    exitCode,
                    SnapshotOutput().StandardOutput,
                    SnapshotOutput().StandardError);
            }

            _exited.TrySetResult(exitCode);
        };
    }

    private async Task WaitForReadinessAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.StartupTimeout);

        Exception? lastConnectionFailure = null;
        while (true)
        {
            if (_process.HasExited)
            {
                var exitCode = await GetObservedExitCodeAsync();
                ThrowStartupFailure(
                    $"Foreign module '{_manifest.Id}' exited with code {exitCode} before readiness.");
            }

            try
            {
                Handshake = await ProtocolClient.HandshakeAsync(
                    _manifest,
                    _runtimeInfo,
                    _options.HostVersion,
                    timeoutCts.Token);
                var discovery = await ProtocolClient.DiscoverAsync(timeoutCts.Token);
                Endpoints = discovery.Endpoints ?? [];
                _moduleProxy.ApplyDiscovery(discovery);
                await RebuildModuleServicesAsync();
                _startupCompleted = true;
                return;
            }
            catch (HttpRequestException ex)
            {
                lastConnectionFailure = ex;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                if (await TryObserveExitCodeAsync(EarlyExitObservationGrace) is { } exitCode)
                {
                    ThrowStartupFailure(
                        $"Foreign module '{_manifest.Id}' exited with code {exitCode} before readiness.",
                        lastConnectionFailure);
                }

                await KillProcessAsync();
                ThrowStartupFailure(
                    $"Foreign module '{_manifest.Id}' did not become ready within {_options.StartupTimeout.TotalSeconds:0.###}s.",
                    lastConnectionFailure);
            }
            catch (ForeignModuleProtocolException ex)
            {
                await KillProcessAsync();
                ThrowStartupFailure(
                    $"Foreign module '{_manifest.Id}' failed protocol readiness: {ex.Message}",
                    ex);
            }

            try
            {
                var delay = Task.Delay(ReadinessPollInterval, timeoutCts.Token);
                var winner = await Task.WhenAny(delay, _exited.Task);
                if (winner == _exited.Task)
                {
                    var exitCode = await GetObservedExitCodeAsync();
                    ThrowStartupFailure(
                        $"Foreign module '{_manifest.Id}' exited with code {exitCode} before readiness.",
                        lastConnectionFailure);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                await KillProcessAsync();
                ThrowStartupFailure(
                    $"Foreign module '{_manifest.Id}' did not become ready within {_options.StartupTimeout.TotalSeconds:0.###}s.",
                    lastConnectionFailure);
            }
        }
    }

    private async Task<int?> TryObserveExitCodeAsync(TimeSpan grace)
    {
        if (_process.HasExited || _exited.Task.IsCompleted)
            return await GetObservedExitCodeAsync();

        if (grace <= TimeSpan.Zero)
            return null;

        var winner = await Task.WhenAny(_exited.Task, Task.Delay(grace));
        return winner == _exited.Task
            ? await GetObservedExitCodeAsync()
            : null;
    }

    private async Task<int> GetObservedExitCodeAsync()
    {
        var exitCode = -1;
        try
        {
            if (_exited.Task.IsCompletedSuccessfully)
                exitCode = await _exited.Task;
            else if (_process.HasExited)
                exitCode = _process.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        WaitForCapturedOutputAfterExit();
        return exitCode;
    }

    private void WaitForCapturedOutputAfterExit()
    {
        try
        {
            if (_process.HasExited)
                _process.WaitForExit();
        }
        catch
        {
        }
    }

    private async Task ShutdownSidecarAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        _draining = true;
        if (Volatile.Read(ref _inFlightCount) == 0)
            _drainTcs.TrySetResult();

        if (_process.HasExited)
            return;

        if (_startupCompleted)
        {
            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            shutdownCts.CancelAfter(_options.ShutdownTimeout);
            try { await ProtocolClient.ShutdownAsync(_manifest, shutdownCts.Token); }
            catch { /* best effort: process ownership is enforced below. */ }
        }

        await WaitForExitOrKillAsync(ct);
    }

    private async Task RebuildModuleServicesAsync()
    {
        var services = new ServiceCollection();
        _moduleProxy.ConfigureServices(services);
        var provider = services.BuildServiceProvider();
        var previous = _serviceProvider;
        _serviceProvider = provider;
        await previous.DisposeAsync();
    }

    private async Task WaitForExitOrKillAsync(CancellationToken ct)
    {
        if (_process.HasExited)
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ShutdownTimeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await KillProcessAsync();
        }
    }

    private async Task KillProcessAsync()
    {
        if (_process.HasExited)
            return;

        try { _process.Kill(entireProcessTree: true); }
        catch { /* already exiting */ }

        await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(2)));
    }

    private async Task DisposeAfterFailedStartupAsync()
    {
        if (!_process.HasExited)
            await KillProcessAsync();

        await _serviceProvider.DisposeAsync();
        if (_capabilityServer is not null)
            await _capabilityServer.DisposeAsync();

        _httpClient.Dispose();
        _process.Dispose();
    }

    private ForeignModuleProcessOutput SnapshotOutput()
    {
        string stdout;
        string stderr;
        lock (_stdoutLock) stdout = _stdout.ToString();
        lock (_stderrLock) stderr = _stderr.ToString();
        return new ForeignModuleProcessOutput(stdout, stderr);
    }

    private void ThrowStartupFailure(string message, Exception? innerException = null) =>
        throw new ForeignModuleStartupException(message, SnapshotOutput(), innerException);

    private void CaptureLine(
        BoundedOutputTail sink,
        object syncRoot,
        string? line,
        bool isError)
    {
        if (line is null)
            return;

        var bounded = SharpClawLogBounds.TruncateUtf8(
            line,
            SharpClawLogBounds.SidecarTailBytes,
            out _);
        lock (syncRoot)
            sink.AppendLine(bounded);

        if (_logger is null)
            return;

        using var ownership = _ownership is null
            ? null
            : SharpClawLogOwnership.Push(_ownership);
        if (isError)
        {
            _logger.LogWarning(
                "Sidecar module stderr: {Line}",
                bounded);
        }
        else
        {
            _logger.LogInformation(
                "Sidecar module stdout: {Line}",
                bounded);
        }
    }

    private static string? TryResolveFileVersion(string executablePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private sealed class BoundedOutputTail
    {
        private readonly StringBuilder _value = new();

        public void AppendLine(string line)
        {
            _value.Append(line).AppendLine();
            var bytes = Encoding.UTF8.GetBytes(_value.ToString());
            if (bytes.Length <= SharpClawLogBounds.SidecarTailBytes)
                return;

            var start = bytes.Length - SharpClawLogBounds.SidecarTailBytes;
            _value.Clear();
            _value.Append(Encoding.UTF8.GetString(bytes, start, bytes.Length - start));
        }

        public override string ToString() => _value.ToString();
    }
}
