using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules.Foreign;

internal sealed class ForeignModuleHost : IForeignModuleRuntimeHost
{
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ModuleManifest _manifest;
    private readonly ModuleManifestRuntimeInfo _runtimeInfo;
    private readonly ForeignModuleHostLaunchOptions _options;
    private readonly Process _process;
    private readonly HttpClient _httpClient;
    private ServiceProvider _serviceProvider;
    private readonly ForeignModuleHostCapabilityServer? _capabilityServer;
    private readonly ForeignModuleProxy _moduleProxy;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _stdoutLock = new();
    private readonly object _stderrLock = new();
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
    }

    public ISharpClawModule Module { get; }
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

        if (runtimeInfo.IsDotNet)
            throw new ArgumentException(
                $"Foreign module host cannot start dotnet module '{manifest.Id}'.",
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

    private void AttachOutputReaders()
    {
        _process.OutputDataReceived += (_, e) => CaptureLine(_stdout, _stdoutLock, e.Data);
        _process.ErrorDataReceived += (_, e) => CaptureLine(_stderr, _stderrLock, e.Data);
        _process.Exited += (_, _) =>
        {
            try { _exited.TrySetResult(_process.ExitCode); }
            catch { _exited.TrySetResult(-1); }
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
                ThrowStartupFailure(
                    $"Foreign module '{_manifest.Id}' exited with code {_process.ExitCode} before readiness.");
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
                    ThrowStartupFailure(
                        $"Foreign module '{_manifest.Id}' exited with code {_exited.Task.Result} before readiness.",
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

    private static void CaptureLine(StringBuilder sink, object syncRoot, string? line)
    {
        if (line is null)
            return;

        lock (syncRoot)
            sink.AppendLine(line);
    }
}
