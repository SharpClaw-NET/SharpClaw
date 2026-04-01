using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Mk8.Shell.Isolation;

/// <summary>
/// WSL2-backed sandbox container for Windows. This is a thin wrapper
/// that runs sandbox processes inside a dedicated WSL2 distro via direct
/// <c>wsl.exe</c> invocations. Inside the distro, processes get the
/// exact same isolation as bare-metal Linux — cgroups v2 (memory, CPU,
/// PIDs, I/O BPS), kernel namespace isolation (PID, mount, IPC, user,
/// UTS), and <c>xt_cgroup</c> iptables/ip6tables network filtering.
/// <list type="bullet">
///   <item><b>Full namespace isolation:</b> PID, mount, IPC, user, UTS
///     — kernel-enforced inside the WSL2 Linux kernel via
///     <c>/opt/mk8shell/run.sh</c> which calls <c>unshare(1)</c>.</item>
///   <item><b>Network iron curtain:</b> <c>xt_cgroup</c> iptables +
///     ip6tables inside WSL2 — traffic is filtered before it leaves
///     the WSL2 VM. The Windows host never sees blocked traffic.
///     Configured by <c>/opt/mk8shell/setup.sh</c>.</item>
///   <item><b>Resource limits:</b> cgroups v2 (memory, CPU, PIDs, I/O
///     BPS) — kernel-enforced, not polled. Created by
///     <c>/opt/mk8shell/setup.sh</c>.</item>
///   <item><b>Filesystem isolation:</b> Mount namespace with read-only
///     root, read-write sandbox directory, read-only tool directories.
///     The sandbox directory resides on the WSL2 Linux filesystem for
///     optimal I/O performance (no 9P overhead).</item>
/// </list>
/// <para>
/// <b>Architecture:</b> Three shell scripts bundled in the distro rootfs
/// implement the container lifecycle:
/// <list type="number">
///   <item><c>/opt/mk8shell/setup.sh</c> — creates cgroup, sets resource
///     limits, creates iptables/ip6tables chains with whitelist rules.
///     Called once from <see cref="StartAsync"/>.</item>
///   <item><c>/opt/mk8shell/run.sh</c> — self-assigns to the cgroup,
///     calls <c>unshare</c> for PID/mount/IPC/user/UTS isolation,
///     applies filesystem isolation (read-only root, bind mounts), then
///     <c>exec</c>s the target command. Called per
///     <see cref="LaunchAsync"/>. The <c>wsl.exe</c> process IS the
///     contained process — stdout/stderr flow naturally to the host.
///   </item>
///   <item><c>/opt/mk8shell/cleanup.sh</c> — kills all PIDs in the
///     cgroup, removes the cgroup directory, flushes iptables chains,
///     removes the sandbox directory. Called from
///     <see cref="StopAsync"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>What this loses vs bare-metal Linux:</b> periodic write-byte
/// polling (replaced by kernel-enforced <c>io.max</c> BPS throttle).
/// </para>
/// <para>
/// <b>Prerequisites:</b> WSL2 must be installed with a Linux kernel
/// present. The mk8shell distro is automatically provisioned on first
/// use from a bundled minimal rootfs. Required tools inside the distro:
/// <c>unshare</c> (util-linux), <c>iptables</c>/<c>ip6tables</c>,
/// <c>modprobe</c> (kmod), cgroups v2 (default in modern kernels),
/// and the three shell scripts above.
/// </para>
/// <para>
/// When WSL2 is unavailable, the factory
/// (<see cref="Mk8SandboxContainer.Create"/>) throws — there is no
/// fallback. mk8.shell requires full VM-style isolation.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Mk8Wsl2SandboxContainer : Mk8SandboxContainer
{
    /// <summary>Name of the dedicated WSL2 distro for mk8.shell sandboxes.</summary>
    private const string DistroName = "mk8shell";

    /// <summary>
    /// Well-known install directory for the mk8shell WSL2 distro.
    /// </summary>
    private static readonly string DistroInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "mk8shell", "wsl2-distro");

    /// <summary>
    /// Well-known path for the bundled minimal rootfs tarball used to
    /// import the mk8shell distro on first run.
    /// </summary>
    private static readonly string BundledRootfsPath = Path.Combine(
        AppContext.BaseDirectory, "Assets", "mk8shell-rootfs.tar.gz");

    /// <summary>
    /// Root path inside the WSL2 distro for sandbox workspaces.
    /// Each sandbox gets a subdirectory under this path.
    /// </summary>
    private const string Wsl2SandboxRoot = "/home/mk8/sandboxes";

    /// <summary>Path to the setup script inside the WSL2 distro.</summary>
    private const string SetupScriptPath = "/opt/mk8shell/setup.sh";

    /// <summary>Path to the run script inside the WSL2 distro.</summary>
    private const string RunScriptPath = "/opt/mk8shell/run.sh";

    /// <summary>Path to the cleanup script inside the WSL2 distro.</summary>
    private const string CleanupScriptPath = "/opt/mk8shell/cleanup.sh";

    private readonly List<Process> _launchedProcesses = [];
    private string? _wsl2SandboxPath;
    private string? _cgroupName;

    public Mk8Wsl2SandboxContainer(
        Mk8ContainerConfig config, string sandboxPath, string sandboxId)
        : base(config, sandboxPath, sandboxId) { }

    // ═══════════════════════════════════════════════════════════════
    // Availability check
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether WSL2 is available and functional on this system.
    /// Does NOT provision the distro — that happens at
    /// <see cref="StartAsync"/>.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            // Check if wsl.exe exists and WSL2 is functional
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                ArgumentList = { "--status" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            process.WaitForExit(10_000);

            // wsl --status exits 0 when WSL2 is installed and has a kernel.
            // Exit code is the reliable signal — the output text is
            // localized and cannot be string-matched reliably.
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public override async Task StartAsync(CancellationToken ct = default)
    {
        if (IsActive)
            throw new Mk8ContainerException("WSL2 container is already active.");

        // Step 1: Ensure the mk8shell distro exists
        await EnsureDistroAsync(ct);

        // Step 2: Create sandbox directory inside WSL2 Linux filesystem
        // (not /mnt/c — native ext4 for performance)
        _wsl2SandboxPath = $"{Wsl2SandboxRoot}/{SanitizeName(SandboxId)}";
        _cgroupName = $"mk8-{SanitizeName(SandboxId)}";

        // Validate the WSL2 sandbox path before ANY shell command uses it
        ValidateWslPath(_wsl2SandboxPath, "WSL2 sandbox directory");

        // Validate all mapped directory paths (defense-in-depth, matching Linux)
        foreach (var dir in Config.MappedDirectories)
        {
            // Windows host paths will be converted via WindowsPathToWslPath,
            // which may contain spaces or special chars. Validate after conversion.
            var wslHostPath = WindowsPathToWslPath(dir.HostPath);
            ValidateWslPath(wslHostPath, "WSL2 mapped host directory");
            ValidateWslPath(dir.ContainerPath, "WSL2 mapped container directory");
        }

        await WslExecArgvAsync("/bin/mkdir", ["-p", _wsl2SandboxPath], ct);

        // Step 3: Copy sandbox contents from Windows to WSL2 filesystem
        // The sandbox directory on Windows has the signed env, scripts, etc.
        // We sync it into the WSL2 native filesystem for I/O performance.
        var wslWindowsPath = WindowsPathToWslPath(SandboxPath);

        // Validate the converted Windows path — it may contain spaces or
        // special characters that survived WindowsPathToWslPath conversion
        ValidateWslPath(wslWindowsPath, "WSL2 Windows mount path");

        await WslExecArgvAsync(
            "/bin/cp", ["-a", $"{wslWindowsPath}/.", $"{_wsl2SandboxPath}/"], ct);

        // Step 4: Build config JSON for setup.sh
        var configJson = BuildSetupConfigJson();

        // Step 5: Run setup.sh to create cgroup + iptables rules
        // setup.sh creates the cgroup, sets resource limits, and
        // configures iptables/ip6tables chains with whitelist rules.
        // No persistent process. No handshake.
        await WslExecArgvAsync(
            SetupScriptPath, [_cgroupName, configJson], ct);

        IsActive = true;
    }

    public override async Task<Mk8ContainedProcess> LaunchAsync(
        ProcessStartInfo startInfo, CancellationToken ct = default)
    {
        if (!IsActive || _wsl2SandboxPath is null || _cgroupName is null)
            throw new Mk8ContainerException(
                "WSL2 container is not active. Call StartAsync first.");

        // Build wsl.exe process that calls run.sh inside the distro.
        // run.sh self-assigns to the cgroup, execs unshare + filesystem
        // isolation + target command. The wsl.exe process IS the
        // contained process — stdout/stderr flow naturally.
        var cgroupPath = $"/sys/fs/cgroup/{_cgroupName}";
        var workDir = startInfo.WorkingDirectory ?? _wsl2SandboxPath;

        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(DistroName);
        psi.ArgumentList.Add("--");

        // Sanitize environment: use env -i inside WSL2 to prevent
        // Windows host environment variables from leaking to sandbox
        // processes. Without this, secrets stored in Windows env vars
        // (API keys, tokens, etc.) are visible inside the distro.
        psi.ArgumentList.Add("/usr/bin/env");
        psi.ArgumentList.Add("-i");
        foreach (var envVar in BuildSafeEnvironment(startInfo))
            psi.ArgumentList.Add($"{envVar.Key}={envVar.Value}");

        psi.ArgumentList.Add(RunScriptPath);
        psi.ArgumentList.Add(cgroupPath);
        psi.ArgumentList.Add(_wsl2SandboxPath);
        psi.ArgumentList.Add(workDir);

        // Mapped directory pairs: hostPath containerPath readOnly ...
        foreach (var dir in Config.MappedDirectories)
        {
            var wslHostPath = WindowsPathToWslPath(dir.HostPath);
            psi.ArgumentList.Add(wslHostPath);
            psi.ArgumentList.Add(dir.ContainerPath);
            psi.ArgumentList.Add(dir.ReadOnly ? "ro" : "rw");
        }

        // Separator between mapped dirs and the target command
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(startInfo.FileName);
        foreach (var arg in startInfo.ArgumentList)
            psi.ArgumentList.Add(arg);

        var wslProcess = new Process { StartInfo = psi };
        wslProcess.Start();

        lock (_launchedProcesses)
            _launchedProcesses.Add(wslProcess);

        return new Mk8ContainedProcess
        {
            Process = wslProcess,
            ContainerId = _wsl2SandboxPath,
        };
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        if (!IsActive && _wsl2SandboxPath is null)
            return;

        IsActive = false;

        // Kill all launched wsl.exe processes (best-effort)
        lock (_launchedProcesses)
        {
            foreach (var proc in _launchedProcesses)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch { }
                proc.Dispose();
            }
            _launchedProcesses.Clear();
        }

        // Sync results back to Windows before cleanup
        if (_wsl2SandboxPath is not null)
        {
            var wslWindowsPath = WindowsPathToWslPath(SandboxPath);
            try
            {
                await WslExecArgvAsync(
                    "/bin/cp", ["-a", $"{_wsl2SandboxPath}/.", $"{wslWindowsPath}/"], default);
            }
            catch { /* best effort — sandbox is being torn down */ }
        }

        // Run cleanup.sh to tear down cgroup + iptables + sandbox dir
        if (_cgroupName is not null && _wsl2SandboxPath is not null)
        {
            try
            {
                await WslExecArgvAsync(
                    CleanupScriptPath, [_cgroupName, _wsl2SandboxPath], default);
            }
            catch { /* best effort */ }
        }

        // Fallback: remove WSL2 sandbox directory if cleanup.sh missed it
        if (_wsl2SandboxPath is not null)
        {
            try
            {
                await WslExecArgvAsync("/bin/rm", ["-rf", _wsl2SandboxPath], default);
            }
            catch { /* best effort */ }
        }

        // Null state fields to prevent double cleanup on DisposeAsync
        _wsl2SandboxPath = null;
        _cgroupName = null;
    }

    public override async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        catch { /* best effort */ }
    }

    // ═══════════════════════════════════════════════════════════════
    // Capability reporting
    // ═══════════════════════════════════════════════════════════════

    public override Mk8ContainerCapability CheckCapability()
    {
        var wslAvailable = IsAvailable();
        var distroReady = IsDistroInstalled();

        // When both WSL2 and the distro are available, the isolation
        // capabilities are identical to bare-metal Linux — because
        // the same cgroups v2 + unshare + iptables code runs inside
        // WSL2 via the bundled shell scripts. We probe for root +
        // tools inside the distro via wsl exec.
        if (wslAvailable && distroReady)
        {
            // Batch all capability checks into a single wsl.exe
            // invocation to avoid spawning multiple processes.
            var (hasRoot, hasUnshare, hasCgroups) = CheckWslCapabilities();

            var supported = hasRoot && hasUnshare && hasCgroups;

            return new Mk8ContainerCapability
            {
                Supported = supported,
                ProcessIsolation = hasRoot && hasUnshare,
                ResourceLimits = hasCgroups,
                NetworkFiltering = hasRoot,
                FilesystemIsolation = hasRoot && hasUnshare,
                IpcIsolation = hasRoot && hasUnshare,
                WriteByteLimits = hasCgroups,
                Notes = supported
                    ? "WSL2 backend: full Linux namespace isolation " +
                      "(PID, mount, IPC, user, UTS) with xt_cgroup " +
                      "network filtering via direct wsl.exe invocations."
                    : "WSL2 distro is installed but missing required " +
                      "capabilities. Ensure root, unshare, and " +
                      "cgroups v2 are available inside the distro.",
            };
        }

        return new Mk8ContainerCapability
        {
            Supported = false,
            ProcessIsolation = false,
            ResourceLimits = false,
            NetworkFiltering = false,
            FilesystemIsolation = false,
            IpcIsolation = false,
            WriteByteLimits = false,
            Notes = !wslAvailable
                ? "WSL2 is not available. Install WSL2 via " +
                  "'wsl --install' and ensure a Linux kernel is present."
                : $"The '{DistroName}' WSL2 distro is not installed. " +
                  "It will be provisioned automatically on first use.",
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Distro management
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether the mk8shell WSL2 distro is already registered.
    /// </summary>
    private static bool IsDistroInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                ArgumentList = { "-l", "-v" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // wsl -l -v outputs UTF-16 LE on Windows
                StandardOutputEncoding = Encoding.Unicode,
            };

            using var process = Process.Start(psi);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            return output.Contains(DistroName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures the mk8shell WSL2 distro exists. If not, imports it
    /// from the bundled rootfs tarball. The distro is configured with
    /// WSL2 as the version and root as the default user.
    /// </summary>
    private async Task EnsureDistroAsync(CancellationToken ct)
    {
        if (IsDistroInstalled())
            return;

        if (!File.Exists(BundledRootfsPath))
            throw new Mk8ContainerException(
                $"Cannot provision WSL2 distro: bundled rootfs not found " +
                $"at '{BundledRootfsPath}'. The mk8.shell installation " +
                $"may be incomplete.");

        // Create install directory
        Directory.CreateDirectory(DistroInstallDir);

        // Import the distro from the rootfs tarball.
        // --import is a host-level WSL command (no -d distro targeting).
        await WslCommandDirectAsync(
            ["--import", DistroName, DistroInstallDir, BundledRootfsPath, "--version", "2"],
            ct);

        // Ensure the distro was registered successfully
        if (!IsDistroInstalled())
            throw new Mk8ContainerException(
                "Failed to import the mk8shell WSL2 distro. " +
                "Check that WSL2 is installed correctly and the " +
                "rootfs tarball is valid.");

        // Install required packages inside the distro.
        // The bundled rootfs is expected to be a minimal image
        // (Alpine-based) with these pre-installed. This is a
        // safety net for custom rootfs builds.
        // Safe to use WslExecShellAsync here — no external paths,
        // only hardcoded tool names and shell constructs.
        await WslExecShellAsync(
            "command -v unshare >/dev/null 2>&1 && " +
            "command -v iptables >/dev/null 2>&1 && " +
            "command -v ip6tables >/dev/null 2>&1 && " +
            "command -v modprobe >/dev/null 2>&1 || " +
            "{ echo 'Missing required tools in mk8shell distro' >&2; exit 1; }",
            ct);

        // Create sandbox root directory
        await WslExecArgvAsync("/bin/mkdir", ["-p", Wsl2SandboxRoot], ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // WSL2 command helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates a WSL2 path to prevent shell injection. Only
    /// alphanumeric characters, forward slash, underscore, period, and
    /// hyphen are allowed. This is identical to the path validation in
    /// <see cref="Mk8LinuxSandboxContainer"/> — the same character set
    /// prevents shell metacharacters from being interpolated into commands.
    /// </summary>
    private static void ValidateWslPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new Mk8ContainerException(
                $"Empty {label} path. All WSL2 paths must be non-empty.");

        foreach (var c in path)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or
                (>= '0' and <= '9') or '/' or '_' or '.' or '-')
                continue;

            throw new Mk8ContainerException(
                $"Invalid character '{c}' (U+{(int)c:X4}) in {label} " +
                $"path: {path}. Only [a-zA-Z0-9/_.-] are allowed to " +
                $"prevent shell injection.");
        }

        // Reject path traversal sequences — the charset allows '.' so
        // ".." sequences are possible. Canonical paths from
        // SanitizeName/Path.GetFullPath won't contain these, but we
        // reject them as a hard guarantee.
        if (path.Contains("/../", StringComparison.Ordinal) ||
            path.StartsWith("../", StringComparison.Ordinal) ||
            path.EndsWith("/..", StringComparison.Ordinal) ||
            path == "..")
        {
            throw new Mk8ContainerException(
                $"Path traversal sequence '..' detected in {label} " +
                $"path: {path}. Parent directory references " +
                $"are not allowed in sandbox paths.");
        }
    }

    /// <summary>
    /// Runs <c>wsl.exe</c> with raw arguments that are NOT targeted at
    /// a specific distro. Used for host-level WSL commands such as
    /// <c>--import</c>, <c>--unregister</c>, <c>--shutdown</c>, etc.
    /// </summary>
    private static async Task WslCommandDirectAsync(
        string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Mk8ContainerException(
                $"WSL2 host command failed (exit {process.ExitCode}): " +
                $"wsl.exe {string.Join(' ', args)}" +
                (string.IsNullOrWhiteSpace(stderr) ? "" : $"\n{stderr.Trim()}"));
    }

    /// <summary>
    /// Executes a command inside the mk8shell WSL2 distro using
    /// argument-list passing (no shell interpolation). This is the
    /// SAFE variant that prevents shell injection — arguments are
    /// passed via <see cref="ProcessStartInfo.ArgumentList"/> and do
    /// not go through <c>/bin/sh -c</c>.
    /// </summary>
    private static async Task WslExecArgvAsync(
        string binary, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(DistroName);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(binary);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Mk8ContainerException(
                $"WSL2 command failed (exit {process.ExitCode}): " +
                $"{binary} {string.Join(' ', args)}" +
                (string.IsNullOrWhiteSpace(stderr) ? "" : $"\n{stderr.Trim()}"));
    }

    /// <summary>
    /// Executes a shell command inside the mk8shell WSL2 distro via
    /// <c>/bin/sh -c</c>. This variant is UNSAFE for paths or any
    /// external input — use <see cref="WslExecArgvAsync"/> instead.
    /// Only use this for hardcoded commands with no interpolated paths.
    /// </summary>
    private static async Task WslExecShellAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(DistroName);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("/bin/sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Mk8ContainerException(
                $"WSL2 command failed (exit {process.ExitCode}): {command}" +
                (string.IsNullOrWhiteSpace(stderr) ? "" : $"\n{stderr.Trim()}"));
    }

    /// <summary>
    /// Checks all required capabilities inside the WSL2 distro in a
    /// single <c>wsl.exe</c> invocation. Returns root, unshare, and
    /// cgroups v2 availability as a colon-separated triplet from
    /// stdout: <c>uid:hasUnshare:hasCgroups</c>.
    /// </summary>
    private static (bool HasRoot, bool HasUnshare, bool HasCgroups) CheckWslCapabilities()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(DistroName);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                "echo $(id -u):$(test -f /usr/bin/unshare && echo 1 || echo 0):" +
                "$(test -d /sys/fs/cgroup && echo 1 || echo 0)");

            using var process = Process.Start(psi);
            if (process is null)
                return (false, false, false);

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);

            if (process.ExitCode != 0)
                return (false, false, false);

            // Parse "0:1:1" → uid=0, unshare=1, cgroups=1
            var parts = output.Split(':');
            if (parts.Length < 3)
                return (false, false, false);

            var hasRoot = parts[0] == "0";
            var hasUnshare = parts[1] == "1";
            var hasCgroups = parts[2] == "1";

            return (hasRoot, hasUnshare, hasCgroups);
        }
        catch
        {
            return (false, false, false);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Config helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the JSON config string passed to <c>setup.sh</c>.
    /// Contains resource limits, mapped directories (with Windows paths
    /// converted to WSL2 paths), and network whitelist rules.
    /// </summary>
    private string BuildSetupConfigJson()
    {
        var configObj = new Dictionary<string, object>
        {
            ["memoryLimitBytes"] = Config.MemoryLimitBytes,
            ["cpuPercentLimit"] = Config.CpuPercentLimit,
            ["maxProcesses"] = Config.MaxProcesses,
            ["maxWriteBytes"] = Config.MaxWriteBytes,
            ["mappedDirectories"] = Config.MappedDirectories.Select(d => new
            {
                hostPath = WindowsPathToWslPath(d.HostPath),
                containerPath = d.ContainerPath,
                readOnly = d.ReadOnly,
            }).ToArray(),
            ["networkWhitelist"] = Config.NetworkWhitelist.Rules.Select(r => new
            {
                host = r.Host,
                port = r.Port,
                protocol = r.Protocol.ToString().ToLowerInvariant(),
            }).ToArray(),
            ["allowAll"] = Config.NetworkWhitelist.AllowAll,
        };

        return JsonSerializer.Serialize(configObj);
    }

    // ═══════════════════════════════════════════════════════════════
    // Environment sanitization
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Allowlist of environment variable names safe to pass into the
    /// WSL2 sandbox. Mirrors the Linux container's SafeEnvKeys.
    /// </summary>
    private static readonly HashSet<string> SafeEnvKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USER", "SHELL", "LANG", "TERM", "LC_ALL",
        "LC_CTYPE", "TZ", "TMPDIR", "XDG_RUNTIME_DIR",
    };

    /// <summary>
    /// Builds the sanitized environment for a sandbox process inside
    /// WSL2. Starts with safe Linux defaults, then copies only safe
    /// variables from the caller's <see cref="ProcessStartInfo"/>.
    /// MK8_-prefixed variables (from the signed sandbox env) are
    /// also passed through.
    /// </summary>
    private static Dictionary<string, string> BuildSafeEnvironment(
        ProcessStartInfo source)
    {
        var safe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["HOME"] = "/home/mk8",
            ["TMPDIR"] = "/tmp",
            ["TERM"] = "xterm",
        };

        foreach (var kvp in source.Environment)
        {
            if (kvp.Value is null) continue;

            if (SafeEnvKeys.Contains(kvp.Key))
            {
                safe[kvp.Key] = kvp.Value;
                continue;
            }

            if (kvp.Key.StartsWith("MK8_", StringComparison.OrdinalIgnoreCase))
            {
                safe[kvp.Key] = kvp.Value;
                continue;
            }
        }

        // Always override TMPDIR to container-local tmpfs
        safe["TMPDIR"] = "/tmp";

        return safe;
    }

    // ═══════════════════════════════════════════════════════════════
    // Path helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts a Windows path (e.g., <c>C:\Users\foo</c>) to the
    /// WSL2 mount path (e.g., <c>/mnt/c/Users/foo</c>). Handles
    /// drive letter and path separator conversion.
    /// </summary>
    private static string WindowsPathToWslPath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath))
            return windowsPath;

        // UNC paths cannot be mapped into WSL2
        if (windowsPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new Mk8ContainerException(
                $"UNC paths are not supported in WSL2: '{windowsPath}'. " +
                "WSL2 can only access local drive-letter paths via /mnt/.");

        // C:\Users\foo → /mnt/c/Users/foo
        if (windowsPath.Length >= 2 && windowsPath[1] == ':')
        {
            var driveLetter = char.ToLowerInvariant(windowsPath[0]);
            var remainder = windowsPath[2..].Replace('\\', '/');
            return $"/mnt/{driveLetter}{remainder}";
        }

        // Relative or already Unix-style
        return windowsPath.Replace('\\', '/');
    }
}
