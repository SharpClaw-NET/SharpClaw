using System.Collections.Concurrent;

namespace Mk8.Shell.Isolation;

/// <summary>
/// Persistent background manager that owns every sandbox container for
/// the lifetime of the mk8.shell process. Containers are created the
/// moment a sandbox is registered and destroyed only when the sandbox
/// is unregistered — they are never lazily started, never
/// reference-counted, and never torn down between commands.
/// <para>
/// <b>Lifecycle guarantees:</b>
/// <list type="bullet">
///   <item><b>Process startup:</b> <see cref="StartAllAsync"/> reads the
///     sandbox registry and creates + starts a container for every
///     registered sandbox. Any stale OS artifacts (cgroups, iptables
///     chains) from a previous unclean shutdown are cleaned up first.</item>
///   <item><b>Sandbox registration:</b> <see cref="StartContainerAsync"/>
///     is called by <c>Mk8SandboxRegistrar.Register</c> to bring the
///     new sandbox's container online immediately.</item>
///   <item><b>Command execution:</b> <see cref="GetContainer"/> returns
///     the already-running container. If the container is not running,
///     the sandbox is not usable — there is no lazy fallback.</item>
///   <item><b>Sandbox deletion:</b> <see cref="StopContainerAsync"/>
///     is called by <c>Mk8SandboxRegistrar.Unregister</c> to tear
///     down the container before the sandbox directory is removed.</item>
///   <item><b>Process shutdown:</b> <see cref="ShutdownAsync"/> tears
///     down ALL active containers. On next startup, they are recreated
///     from the registry.</item>
/// </list>
/// </para>
/// <para>
/// mk8.shell is either fully running (all sandboxes containerized) or
/// entirely disabled. There is no partial mode, no ephemeral mode, and
/// no per-sandbox opt-out.
/// </para>
/// </summary>
public sealed class Mk8ContainerManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Mk8SandboxContainer> _containers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _running;
    private bool _disposed;

    // ═══════════════════════════════════════════════════════════════
    // Singleton
    // ═══════════════════════════════════════════════════════════════

    private static readonly object InstanceLock = new();
    private static Mk8ContainerManager? _instance;

    /// <summary>
    /// Returns the process-level singleton. The instance is created on
    /// first access but containers are NOT started until
    /// <see cref="StartAllAsync"/> is called explicitly at process
    /// startup.
    /// </summary>
    public static Mk8ContainerManager Instance
    {
        get
        {
            if (_instance is not null)
                return _instance;

            lock (InstanceLock)
                return _instance ??= new Mk8ContainerManager();
        }
    }

    /// <summary>
    /// Replaces the singleton instance. Used only for testing.
    /// </summary>
    internal static void ResetInstance(Mk8ContainerManager? manager = null)
    {
        lock (InstanceLock)
        {
            _instance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _instance = manager;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Startup — bring ALL sandboxes online
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the sandbox registry and starts a container for every
    /// registered sandbox. Must be called once at mk8.shell process
    /// startup before any commands are accepted.
    /// <para>
    /// Stale OS artifacts from a previous unclean shutdown are cleaned
    /// up before new containers are created.
    /// </para>
    /// </summary>
    public async Task StartAllAsync(
        Models.Mk8SandboxRegistry? registry = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (_running)
                return;

            // Clean up stale OS artifacts from previous unclean shutdown
            await RecoverStaleContainersAsync(ct);

            registry ??= new Models.Mk8SandboxRegistry();
            var globalEnv = Models.Mk8GlobalEnv.Load();
            var globalContainerConfig = globalEnv.ToContainerConfig();

            var sandboxes = registry.LoadSandboxes();

            foreach (var (sandboxId, entry) in sandboxes)
            {
                var sandboxRoot = Path.GetFullPath(entry.RootPath);
                if (!Directory.Exists(sandboxRoot))
                    continue; // Sandbox directory missing — skip, don't crash startup

                try
                {
                    var containerConfig = BuildContainerConfig(
                        globalEnv, globalContainerConfig, sandboxRoot, registry);

                    var container = Mk8SandboxContainer.Create(
                        containerConfig, sandboxRoot, sandboxId);
                    await container.StartAsync(ct);

                    _containers[sandboxId] = container;
                }
                catch (Exception ex)
                {
                    // Log and continue — one broken sandbox must not
                    // prevent the rest from starting.
                    System.Diagnostics.Debug.WriteLine(
                        $"[mk8.shell] Failed to start container for sandbox " +
                        $"'{sandboxId}': {ex.Message}",
                        "Mk8.Shell.Isolation");
                }
            }

            _running = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Per-sandbox lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates and starts a container for a newly registered sandbox.
    /// Called by <c>Mk8SandboxRegistrar.Register</c> immediately after
    /// the sandbox directory and signed env are created. The container
    /// is active from this point forward.
    /// </summary>
    public async Task StartContainerAsync(
        string sandboxId,
        string sandboxPath,
        Mk8ContainerConfig config,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);
        ArgumentNullException.ThrowIfNull(config);

        await _gate.WaitAsync(ct);
        try
        {
            // If already running (e.g. re-registration), stop the old one first
            if (_containers.TryRemove(sandboxId, out var old))
            {
                try { await old.StopAsync(ct); }
                catch { /* best effort */ }
            }

            var container = Mk8SandboxContainer.Create(config, sandboxPath, sandboxId);
            await container.StartAsync(ct);
            _containers[sandboxId] = container;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops and removes the container for a sandbox being unregistered.
    /// Called by <c>Mk8SandboxRegistrar.Unregister</c> before the
    /// sandbox directory is removed. All contained processes are killed,
    /// the cgroup is deleted, and iptables chains are flushed.
    /// </summary>
    public async Task StopContainerAsync(
        string sandboxId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);

        await _gate.WaitAsync(ct);
        try
        {
            if (_containers.TryRemove(sandboxId, out var container))
            {
                await container.StopAsync(ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the running container for the given sandbox. Throws if
    /// the container is not active — there is no lazy startup.
    /// A sandbox without a running container is not usable.
    /// </summary>
    public Mk8SandboxContainer GetContainer(string sandboxId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);

        if (!_containers.TryGetValue(sandboxId, out var container) || !container.IsActive)
        {
            throw new Mk8ContainerException(
                $"No active container for sandbox '{sandboxId}'. " +
                "The mk8.shell process must be running and the sandbox " +
                "must be registered. Check that mk8.shell startup " +
                "completed successfully.");
        }

        return container;
    }

    /// <summary>
    /// Returns <c>true</c> if a container is actively running for the
    /// given sandbox ID.
    /// </summary>
    public bool IsContainerActive(string sandboxId) =>
        _containers.TryGetValue(sandboxId, out var c) && c.IsActive;

    // ═══════════════════════════════════════════════════════════════
    // Shutdown
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Tears down ALL active containers. Called on mk8.shell process
    /// exit. Kills all sandbox processes, removes cgroups, flushes
    /// iptables. On next startup, <see cref="StartAllAsync"/>
    /// recreates them from the registry.
    /// </summary>
    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var (_, container) in _containers)
            {
                try { await container.StopAsync(); }
                catch { /* best effort */ }
            }
            _containers.Clear();
            _running = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether the manager has completed startup.</summary>
    public bool IsRunning => _running;

    /// <summary>Number of actively managed containers.</summary>
    public int ActiveCount => _containers.Count(kv => kv.Value.IsActive);

    /// <summary>Sandbox IDs of all actively managed containers.</summary>
    public IReadOnlyList<string> ActiveSandboxIds =>
        _containers
            .Where(kv => kv.Value.IsActive)
            .Select(kv => kv.Key)
            .ToList();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await ShutdownAsync();
        _gate.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // Stale container recovery
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Cleans up OS-level container artifacts (cgroups, iptables chains,
    /// WSL2 sandbox directories) left behind by a previous unclean
    /// shutdown. Called once at startup before new containers are created.
    /// </summary>
    private static async Task RecoverStaleContainersAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
            await RecoverLinuxContainersAsync(ct);
        else if (OperatingSystem.IsWindows())
            await RecoverWsl2ContainersAsync(ct);
    }

    private static async Task RecoverLinuxContainersAsync(CancellationToken ct)
    {
        const string cgroupRoot = "/sys/fs/cgroup";
        if (!Directory.Exists(cgroupRoot))
            return;

        foreach (var dir in Directory.GetDirectories(cgroupRoot, "mk8shell-*"))
        {
            var cgroupName = Path.GetFileName(dir);
            try
            {
                var procsFile = Path.Combine(dir, "cgroup.procs");
                if (File.Exists(procsFile))
                {
                    var pids = await File.ReadAllTextAsync(procsFile, ct);
                    foreach (var line in pids.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(line.Trim(), out var pid))
                        {
                            try { System.Diagnostics.Process.GetProcessById(pid).Kill(true); }
                            catch { }
                        }
                    }
                }

                await Task.Delay(100, ct);
                try { Directory.Delete(dir, recursive: false); }
                catch { }

                foreach (var iptables in (string[])["iptables", "ip6tables"])
                {
                    try { await RunBestEffortAsync(iptables, ["-D", "OUTPUT", "-m", "cgroup", "--path", cgroupName, "-j", cgroupName]); }
                    catch { }
                    try { await RunBestEffortAsync(iptables, ["-F", cgroupName]); }
                    catch { }
                    try { await RunBestEffortAsync(iptables, ["-X", cgroupName]); }
                    catch { }
                }
            }
            catch { /* best effort recovery */ }
        }
    }

    private static async Task RecoverWsl2ContainersAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (!Mk8Wsl2SandboxContainer.IsAvailable())
            return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add("mk8shell");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("/opt/mk8shell/cleanup.sh");
            psi.ArgumentList.Add("--recover-stale");

            using var process = new System.Diagnostics.Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync(ct);
        }
        catch { /* WSL2 distro may not be provisioned yet */ }
    }

    // ═══════════════════════════════════════════════════════════════
    // Config helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the effective container config for a sandbox by merging
    /// global config with sandbox-level network whitelist overrides.
    /// </summary>
    private static Mk8ContainerConfig BuildContainerConfig(
        Models.Mk8GlobalEnv globalEnv,
        Mk8ContainerConfig globalContainerConfig,
        string sandboxRoot,
        Models.Mk8SandboxRegistry registry)
    {
        try
        {
            var signedEnvPath = Path.Combine(
                sandboxRoot, Models.Mk8SandboxRegistry.SandboxSignedEnvFileName);

            if (!File.Exists(signedEnvPath))
                return globalContainerConfig;

            var key = registry.LoadKey();
            var signedContent = File.ReadAllText(signedEnvPath);
            var envContent = Models.Mk8SandboxEnvSigner.VerifyAndExtract(signedContent, key);
            var sandboxVars = Models.Mk8SandboxEnvParser.Parse(envContent);

            var sandboxNetworkWhitelist = sandboxVars.GetValueOrDefault("MK8_NETWORK_WHITELIST");
            if (sandboxNetworkWhitelist is null)
                return globalContainerConfig;

            var sandboxContainerConfig = new Mk8ContainerConfig
            {
                NetworkWhitelist = Mk8NetworkWhitelist.Parse(sandboxNetworkWhitelist),
            };

            return globalContainerConfig.TightenWith(sandboxContainerConfig);
        }
        catch
        {
            // If we can't read the sandbox env, use global config.
            // The container will still be created with full isolation.
            return globalContainerConfig;
        }
    }

    private static async Task RunBestEffortAsync(string binary, string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.Start();
        await process.WaitForExitAsync();
    }
}
