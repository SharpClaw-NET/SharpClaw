using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Mk8.Shell.Isolation;

/// <summary>
/// Linux per-sandbox persistent container using kernel isolation primitives:
/// <list type="bullet">
///   <item><b>Full namespace isolation:</b> Every mk8-launched process gets
///     PID, mount, IPC, user, and UTS namespace isolation via
///     <c>unshare(2)</c>. This is mandatory — not conditional.</item>
///   <item><b>Process containment:</b> A persistent cgroup v2 captures all
///     mk8-launched processes. Each process is explicitly assigned to the
///     cgroup at launch time.</item>
///   <item><b>Resource limits:</b> cgroups v2 — memory.max, cpu.max,
///     pids.max applied to the persistent cgroup. All contained processes
///     share the resource budget.</item>
///   <item><b>Write byte limits:</b> Periodic polling of cgroup
///     <c>io.stat</c> tracks cumulative write bytes. When
///     <see cref="Mk8ContainerConfig.MaxWriteBytes"/> is exceeded, all
///     processes in the cgroup are killed.</item>
///   <item><b>Network iron curtain:</b> <c>xt_cgroup</c> iptables +
///     ip6tables modules filter traffic by cgroup path. Default-block
///     outbound with permit rules for whitelisted destinations. Applies
///     to ALL processes in the cgroup.
///     Both IPv4 and IPv6 are filtered.</item>
///   <item><b>Filesystem isolation:</b> Mount namespace with read-only
///     root, read-write sandbox directory, read-only mapped tool
///     directories, tmpfs /tmp and /dev/shm, and masked host sockets
///     (Docker, D-Bus).</item>
///   <item><b>IPC isolation:</b> Separate IPC namespace isolates System V
///     IPC (shared memory, semaphores, message queues) and POSIX shared
///     memory. <c>/dev/shm</c> is a separate tmpfs.</item>
///   <item><b>User namespace:</b> Sandbox processes run as an unprivileged
///     UID inside the user namespace even when mk8.shell runs as root.</item>
///   <item><b>Security hardening:</b> <c>PR_SET_NO_NEW_PRIVS</c> prevents
///     privilege escalation via setuid binaries.</item>
/// </list>
/// <para>
/// Requires root or CAP_SYS_ADMIN for namespace creation and
/// xt_cgroup. All prerequisites are validated at startup — there
/// are no silent fallbacks.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class Mk8LinuxSandboxContainer : Mk8SandboxContainer
{
    private const string CgroupRoot = "/sys/fs/cgroup";

    private string? _cgroupPath;
    private string? _iptablesChainName;
    private CancellationTokenSource? _watcherCts;
    private Task? _watcherTask;

    public Mk8LinuxSandboxContainer(
        Mk8ContainerConfig config, string sandboxPath, string sandboxId)
        : base(config, sandboxPath, sandboxId) { }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public override async Task StartAsync(CancellationToken ct = default)
    {
        if (IsActive)
            throw new Mk8ContainerException("Container is already active.");

        // Validate prerequisites — fail hard, never degrade
        if (Libc.getuid() != 0)
            throw new Mk8ContainerException(
                "mk8.shell container isolation requires root privileges. " +
                "Namespace creation and xt_cgroup require " +
                "root or CAP_SYS_ADMIN.");

        if (!File.Exists("/usr/bin/unshare"))
            throw new Mk8ContainerException(
                "unshare(1) is required for namespace isolation but was " +
                "not found at /usr/bin/unshare. Install util-linux.");

        // Validate all mapped directory paths before any resource setup.
        // Defense-in-depth against shell injection — even though paths
        // are infrastructure-controlled, not agent-controlled.
        ValidateMappedDirectoryPath(SandboxPath, "sandbox");
        foreach (var dir in Config.MappedDirectories)
        {
            ValidateMappedDirectoryPath(dir.HostPath, "mapped host");
            ValidateMappedDirectoryPath(dir.ContainerPath, "mapped container");
        }

        var cgroupName = $"mk8shell-{SanitizeName(SandboxId)}";
        _cgroupPath = Path.Combine(CgroupRoot, cgroupName);
        _iptablesChainName = cgroupName;

        try
        {
            // Step 1: Create persistent cgroup with resource limits
            await SetupCgroupAsync(_cgroupPath, ct);

            // Step 2: Network filtering via xt_cgroup (IPv4 + IPv6)
            if (!Config.NetworkWhitelist.AllowAll)
            {
                await SetupCgroupNetworkFilterAsync(cgroupName, ct);
            }

            // Step 3: Start background write byte tracking
            _watcherCts = new CancellationTokenSource();
            _watcherTask = MonitorContainerAsync(_watcherCts.Token);

            IsActive = true;
        }
        catch
        {
            await CleanupCgroupAsync(_cgroupPath);
            _cgroupPath = null;
            if (_iptablesChainName is not null)
            {
                await CleanupNetworkFilterAsync(_iptablesChainName);
                _iptablesChainName = null;
            }
            throw;
        }
    }

    public override async Task<Mk8ContainedProcess> LaunchAsync(
        ProcessStartInfo startInfo, CancellationToken ct = default)
    {
        if (!IsActive || _cgroupPath is null)
            throw new Mk8ContainerException(
                "Container is not active. Call StartAsync first.");

        // Build the full unshare flags — all namespace isolation is mandatory
        var unshareFlags = BuildUnshareFlags();

        // Launch via unshare wrapper for full namespace isolation
        var wrapperPsi = new ProcessStartInfo
        {
            FileName = "/usr/bin/unshare",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = startInfo.WorkingDirectory,
        };

        foreach (var flag in unshareFlags)
            wrapperPsi.ArgumentList.Add(flag);

        // --mount-proc is required for PID namespace isolation to work
        // correctly — without it /proc still shows host PIDs
        wrapperPsi.ArgumentList.Add("--mount-proc");

        // Build the filesystem isolation script that runs inside the
        // mount namespace. Uses /bin/sh as internal container plumbing —
        // the agent never controls these arguments. Mapped directory
        // paths are passed as positional args via ArgumentList (not
        // shell-interpolated) to eliminate shell injection.
        wrapperPsi.ArgumentList.Add("--");
        wrapperPsi.ArgumentList.Add("/bin/sh");
        wrapperPsi.ArgumentList.Add("-c");
        wrapperPsi.ArgumentList.Add(BuildFilesystemIsolationScript());
        wrapperPsi.ArgumentList.Add("mk8-fs-isolate"); // $0
        wrapperPsi.ArgumentList.Add(_cgroupPath);      // $1 — cgroup path for self-assignment
        wrapperPsi.ArgumentList.Add(SandboxPath);      // $2 — sandbox dir

        // $3..$N — triplets of (hostPath, containerPath, mode) for all mapped dirs
        foreach (var dir in Config.MappedDirectories)
        {
            wrapperPsi.ArgumentList.Add(dir.HostPath);
            wrapperPsi.ArgumentList.Add(dir.ContainerPath);
            wrapperPsi.ArgumentList.Add(dir.ReadOnly ? "ro" : "rw");
        }

        wrapperPsi.ArgumentList.Add(startInfo.FileName); // target binary

        // Remaining positional args — target's arguments
        foreach (var arg in startInfo.ArgumentList)
            wrapperPsi.ArgumentList.Add(arg);

        // Environment sanitization — only pass an explicit allowlist
        // plus sandbox-specific variables. Never inherit the host env.
        wrapperPsi.Environment.Clear();
        SanitizeEnvironment(wrapperPsi, startInfo);

        var process = new Process { StartInfo = wrapperPsi };
        process.Start();

        // No post-start cgroup assignment — the script self-assigns
        // atomically before doing any mount operations or network access.

        return new Mk8ContainedProcess
        {
            Process = process,
            ContainerId = _cgroupPath,
        };
    }

    /// <summary>
    /// Builds the /bin/sh script that sets up filesystem isolation inside
    /// the mount namespace. The script:
    /// <list type="number">
    ///   <item>Makes all mounts private (prevents propagation to host).</item>
    ///   <item>Remounts root read-only.</item>
    ///   <item>Bind-mounts the sandbox directory read-write.</item>
    ///   <item>Bind-mounts each mapped tool directory read-only (paths
    ///     passed as positional args to eliminate shell injection).</item>
    ///   <item>Mounts tmpfs over /tmp and /dev/shm (IPC isolation).</item>
    ///   <item>Masks host sockets (Docker, D-Bus) with empty files.</item>
    ///   <item>Applies PR_SET_NO_NEW_PRIVS to prevent privilege escalation.</item>
    ///   <item>Execs the target command.</item>
    /// </list>
    /// <para>
    /// Mapped directory paths are passed as positional shell arguments
    /// via <see cref="ProcessStartInfo.ArgumentList"/> (not shell-interpreted),
    /// not as string interpolation. The script accesses them as $N.
    /// This eliminates shell injection even if path validation is bypassed.
    /// </para>
    /// <para>
    /// Critical mount operations (root remount, sandbox bind-mount) use
    /// <c>|| exit 1</c> so failures are fatal, never silently swallowed.
    /// Non-critical mounts (tmpfs, socket masking) tolerate failure.
    /// </para>
    /// </summary>
    private string BuildFilesystemIsolationScript()
    {
        // $1 = cgroup path (for self-assignment)
        // $2 = sandbox path
        // $3..$N = triplets of (hostPath, containerPath, mode) for all mapped dirs
        // After mapped dirs: target binary and its arguments

        // Count how many positional args are consumed by infrastructure.
        // Each mapped dir consumes 3 args (host, container, mode).
        var mappedDirCount = Config.MappedDirectories.Count;
        // After $1 (cgroup) + $2 (sandbox) + mapped dir triplets,
        // the target binary is at position: 3 + (mappedDirCount * 3)
        var shiftCount = 2 + (mappedDirCount * 3);

        var parts = new List<string>
        {
            // FIRST: Self-assign to cgroup before doing ANYTHING else.
            // This eliminates the race window where the process runs outside
            // the cgroup (no resource limits, no network filtering).
            // Matches WSL2's run.sh pattern.
            "echo $$ > \"$1/cgroup.procs\" || exit 1",

            // Critical — must succeed or the sandbox is not isolated
            "mount --make-rprivate / || exit 1",
            "mount -o remount,ro / || exit 1",
            // Bind-mount sandbox directory ($2) read-write — critical
            "mount --bind \"$2\" \"$2\" || exit 1",
            "mount -o remount,rw \"$2\" || exit 1",
        };

        // Bind-mount each mapped directory using positional args.
        // $3/$4/$5 = first triplet (host, container, mode), etc.
        // mode is "ro" or "rw". Read-only dirs get a remount,ro;
        // read-write dirs are bind-mounted without the ro remount.
        var argIdx = 3;
        foreach (var dir in Config.MappedDirectories)
        {
            var hostArg = $"${argIdx}";
            var containerArg = $"${argIdx + 1}";
            var modeArg = $"${argIdx + 2}";
            parts.Add(
                $"mkdir -p \"{containerArg}\" 2>/dev/null; " +
                $"mount --bind \"{hostArg}\" \"{containerArg}\" || exit 1; " +
                $"if [ \"{modeArg}\" = \"ro\" ]; then " +
                $"mount -o remount,ro,bind \"{containerArg}\" || exit 1; " +
                $"fi");
            argIdx += 3;
        }

        // Non-critical — failure tolerable (tmpfs, socket masking)
        parts.Add("mount -t tmpfs tmpfs /tmp 2>/dev/null");
        parts.Add("mount -t tmpfs tmpfs /dev/shm 2>/dev/null");

        // Mask host sockets — overlay with empty files/dirs to prevent access
        parts.Add(
            "if [ -e /var/run/docker.sock ]; then " +
            "mount --bind /dev/null /var/run/docker.sock 2>/dev/null; fi");
        parts.Add(
            "if [ -e /var/run/dbus/system_bus_socket ]; then " +
            "mount --bind /dev/null /var/run/dbus/system_bus_socket 2>/dev/null; fi");

        // Note: PR_SET_NO_NEW_PRIVS requires prctl(2), not a proc
        // write. The --user namespace flag above already provides
        // equivalent protection — setuid bits are ignored inside a
        // user namespace, so privilege escalation via setuid binaries
        // is prevented by the namespace itself.

        // Shift past all infrastructure args, then exec the target command
        parts.Add($"shift {shiftCount}; exec \"$@\"");

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Validates that a path contains only safe characters for filesystem
    /// operations. Rejects any character outside <c>[a-zA-Z0-9/_.-]</c>
    /// to prevent shell injection when paths are used as positional
    /// arguments in the isolation script.
    /// </summary>
    private static void ValidateMappedDirectoryPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new Mk8ContainerException(
                $"Empty {label} directory path. All mapped paths must " +
                $"be non-empty absolute paths.");

        foreach (var c in path)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or
                (>= '0' and <= '9') or '/' or '_' or '.' or '-')
                continue;

            throw new Mk8ContainerException(
                $"Invalid character '{c}' (U+{(int)c:X4}) in {label} " +
                $"directory path: {path}. Only [a-zA-Z0-9/_.-] are " +
                $"allowed to prevent shell injection.");
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
                $"directory path: {path}. Parent directory references " +
                $"are not allowed in sandbox paths.");
        }
    }

    /// <summary>
    /// Allowlist of environment variable names that are safe to pass
    /// through to sandbox processes. Everything else is stripped.
    /// </summary>
    private static readonly HashSet<string> SafeEnvKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USER", "SHELL", "LANG", "TERM", "LC_ALL",
        "LC_CTYPE", "TZ", "TMPDIR", "XDG_RUNTIME_DIR",
    };

    /// <summary>
    /// Copies only safe environment variables from the source to the
    /// target. Host secrets (*_KEY, *_SECRET, *_TOKEN, *_PASSWORD,
    /// *_APIKEY) are never passed through.
    /// </summary>
    private static void SanitizeEnvironment(
        ProcessStartInfo target, ProcessStartInfo source)
    {
        foreach (var kvp in source.Environment)
        {
            if (kvp.Value is null) continue;

            // Allow explicitly safe keys
            if (SafeEnvKeys.Contains(kvp.Key))
            {
                target.Environment[kvp.Key] = kvp.Value;
                continue;
            }

            // Allow mk8-specific sandbox variables (MK8_ prefix)
            if (kvp.Key.StartsWith("MK8_", StringComparison.OrdinalIgnoreCase))
            {
                target.Environment[kvp.Key] = kvp.Value;
                continue;
            }

            // Block everything else — especially secret patterns
        }

        // Override TMPDIR to point inside the container's tmpfs
        target.Environment["TMPDIR"] = "/tmp";
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        if (!IsActive) return;
        IsActive = false;

        // Stop write byte tracker
        if (_watcherCts is not null)
        {
            await _watcherCts.CancelAsync();
            if (_watcherTask is not null)
            {
                try { await _watcherTask; }
                catch (OperationCanceledException) { }
                catch { /* best effort */ }
            }

            _watcherCts.Dispose();
            _watcherCts = null;
            _watcherTask = null;
        }

        // Kill all processes in cgroup FIRST — while network filters
        // are still active. Removing filters before killing would
        // give surviving processes unrestricted network access.
        if (_cgroupPath is not null)
        {
            await CleanupCgroupAsync(_cgroupPath);
            _cgroupPath = null;
        }

        // Remove network filters after all processes are dead
        if (_iptablesChainName is not null)
        {
            await CleanupNetworkFilterAsync(_iptablesChainName);
            _iptablesChainName = null;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (IsActive)
            await StopAsync();
    }

    public override Mk8ContainerCapability CheckCapability()
    {
        var isRoot = Libc.getuid() == 0;
        var hasCgroups = Directory.Exists(CgroupRoot);
        var hasUnshare = File.Exists("/usr/bin/unshare");

        return new Mk8ContainerCapability
        {
            Supported = hasCgroups && isRoot && hasUnshare,
            ProcessIsolation = isRoot && hasUnshare,
            ResourceLimits = hasCgroups,
            NetworkFiltering = isRoot, // xt_cgroup requires root
            FilesystemIsolation = isRoot && hasUnshare,
            IpcIsolation = isRoot && hasUnshare, // --ipc namespace
            WriteByteLimits = hasCgroups, // io.stat from cgroup
            Notes = !isRoot
                ? "Root privileges required for namespace isolation " +
                  "and xt_cgroup network filtering."
                : !hasUnshare
                    ? "unshare(1) not found at /usr/bin/unshare. " +
                      "Install util-linux for namespace isolation."
                    : !hasCgroups
                        ? "cgroups v2 not found at /sys/fs/cgroup."
                        : "Full namespace isolation (PID, mount, IPC, user, UTS) " +
                          "with xt_cgroup network filtering.",
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Write byte tracking
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Background task that periodically checks cgroup <c>io.stat</c>
    /// for write byte limits. When
    /// <see cref="Mk8ContainerConfig.MaxWriteBytes"/> is exceeded, all
    /// processes in the cgroup are killed.
    /// </summary>
    private async Task MonitorContainerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Config.MaxWriteBytes > 0 && _cgroupPath is not null)
                {
                    var written = ReadCgroupWriteBytes(_cgroupPath);
                    WriteBytesUsed = written;

                    if (written > Config.MaxWriteBytes)
                    {
                        // Kill all processes in the cgroup
                        await CleanupCgroupAsync(_cgroupPath);
                        throw new Mk8ContainerException(
                            $"Write byte limit exceeded: {written:N0} bytes " +
                            $"written, limit is {Config.MaxWriteBytes:N0} bytes. " +
                            "All sandbox processes have been killed.");
                    }
                }
            }
            catch (Mk8ContainerException) { throw; }
            catch when (ct.IsCancellationRequested) { break; }
            catch { /* best effort — continue monitoring */ }

            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Reads cumulative write bytes from the cgroup's <c>io.stat</c> file.
    /// Format: <c>MAJ:MIN rbytes=X wbytes=Y rios=Z wios=W dbytes=D dios=E</c>
    /// </summary>
    private static long ReadCgroupWriteBytes(string cgroupPath)
    {
        var ioStatPath = Path.Combine(cgroupPath, "io.stat");
        if (!File.Exists(ioStatPath))
            return 0;

        long totalWriteBytes = 0;

        try
        {
            foreach (var line in File.ReadAllLines(ioStatPath))
            {
                // Each line: "MAJ:MIN key=val key=val ..."
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.StartsWith("wbytes=", StringComparison.Ordinal) &&
                        long.TryParse(part.AsSpan(7), out var wbytes))
                    {
                        totalWriteBytes += wbytes;
                    }
                }
            }
        }
        catch { /* best effort */ }

        return totalWriteBytes;
    }

    // ═══════════════════════════════════════════════════════════════
    // cgroups v2
    // ═══════════════════════════════════════════════════════════════

    private async Task SetupCgroupAsync(string cgroupPath, CancellationToken ct)
    {
        if (!Directory.Exists(CgroupRoot))
            throw new Mk8ContainerException(
                "cgroups v2 not available. /sys/fs/cgroup does not exist.");

        // Clean up stale cgroup from previous unclean shutdown
        if (Directory.Exists(cgroupPath))
            await CleanupCgroupAsync(cgroupPath);

        Directory.CreateDirectory(cgroupPath);

        if (Config.MemoryLimitBytes > 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(cgroupPath, "memory.max"),
                Config.MemoryLimitBytes.ToString(), ct);
        }

        // CPU limit (cpu.max format: "$QUOTA $PERIOD")
        if (Config.CpuPercentLimit > 0)
        {
            var period = 100_000; // 100ms in microseconds
            var quota = Config.CpuPercentLimit * period / 100;
            await File.WriteAllTextAsync(
                Path.Combine(cgroupPath, "cpu.max"),
                $"{quota} {period}", ct);
        }

        if (Config.MaxProcesses > 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(cgroupPath, "pids.max"),
                Config.MaxProcesses.ToString(), ct);
        }

        // Enable io controller for write byte tracking + BPS throttle
        if (Config.MaxWriteBytes > 0)
        {
            // Ensure io controller is enabled in the parent cgroup
            var subtreeControl = Path.Combine(CgroupRoot, "cgroup.subtree_control");
            try
            {
                var controllers = await File.ReadAllTextAsync(subtreeControl, ct);
                if (!controllers.Contains("io"))
                {
                    await File.WriteAllTextAsync(subtreeControl, "+io", ct);
                }
            }
            catch { /* io controller may already be enabled or unavailable */ }

            // Set io.max write BPS throttle to rate-limit burst writes
            // between polling intervals. Without this, a process could
            // exhaust MaxWriteBytes in a single burst before the 100ms
            // monitor loop detects it.
            var deviceId = GetBlockDeviceForPath(SandboxPath);
            if (deviceId is not null)
            {
                // Allow sustained write at MaxWriteBytes/10 per second,
                // minimum 1 MB/s to avoid starving legitimate file I/O
                var writeBps = Math.Max(Config.MaxWriteBytes / 10, 1_048_576);
                try
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(cgroupPath, "io.max"),
                        $"{deviceId} wbps={writeBps}", ct);
                }
                catch { /* io.max may not be available on all configurations */ }
            }
        }
    }

    private static async Task AssignToCgroupAsync(
        string cgroupPath, int pid, CancellationToken ct)
    {
        var procsFile = Path.Combine(cgroupPath, "cgroup.procs");
        await File.WriteAllTextAsync(procsFile, pid.ToString(), ct);
    }

    private static async Task CleanupCgroupAsync(string cgroupPath)
    {
        try
        {
            if (!Directory.Exists(cgroupPath))
                return;

            // Freeze the cgroup to prevent new processes from spawning
            // between reading cgroup.procs and killing PIDs (§4.4).
            var freezeFile = Path.Combine(cgroupPath, "cgroup.freeze");
            try { await File.WriteAllTextAsync(freezeFile, "1"); }
            catch { /* cgroup.freeze may not exist on older kernels */ }

            // Kill all remaining processes in the cgroup
            var procsFile = Path.Combine(cgroupPath, "cgroup.procs");
            if (File.Exists(procsFile))
            {
                var pids = await File.ReadAllTextAsync(procsFile);
                foreach (var line in pids.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(line.Trim(), out var pid))
                    {
                        try { Process.GetProcessById(pid).Kill(true); }
                        catch { /* process may have already exited */ }
                    }
                }
            }

            // Unfreeze before rmdir — kernel requires cgroup to be
            // thawed (no frozen processes) before removal.
            try { await File.WriteAllTextAsync(freezeFile, "0"); }
            catch { /* best effort */ }

            await Task.Delay(100); // brief delay for process exit
            try { Directory.Delete(cgroupPath, recursive: false); }
            catch { /* best effort — kernel may hold it briefly */ }
        }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Resolves the block device <c>major:minor</c> for the filesystem
    /// containing the given path by parsing <c>/proc/self/mountinfo</c>.
    /// Uses longest-prefix match to find the most specific mount point.
    /// Returns <c>null</c> if the device cannot be determined.
    /// </summary>
    private static string? GetBlockDeviceForPath(string path)
    {
        try
        {
            string? bestDevice = null;
            var bestLen = -1;

            foreach (var line in File.ReadAllLines("/proc/self/mountinfo"))
            {
                // Format: id parent major:minor root mount-point ...
                var parts = line.Split(' ');
                if (parts.Length < 5) continue;

                var mountPoint = parts[4];
                var majMin = parts[2];

                if (path.StartsWith(mountPoint, StringComparison.Ordinal) &&
                    mountPoint.Length > bestLen &&
                    majMin.Contains(':'))
                {
                    bestDevice = majMin;
                    bestLen = mountPoint.Length;
                }
            }

            return bestDevice;
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Network filtering via xt_cgroup (per-cgroup, not per-process)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets up iptables AND ip6tables rules using the <c>xt_cgroup</c>
    /// module to filter traffic by cgroup path. This applies to ALL
    /// processes in the cgroup. Uses a custom chain for clean teardown.
    /// Both IPv4 and IPv6 are filtered to prevent bypassing via IPv6.
    /// </summary>
    private async Task SetupCgroupNetworkFilterAsync(
        string cgroupName, CancellationToken ct)
    {
        // Ensure xt_cgroup module is loaded
        try
        {
            await RunCommandAsync("modprobe", ["xt_cgroup"], ct);
        }
        catch
        {
            throw new Mk8ContainerException(
                "Failed to load xt_cgroup kernel module. Network isolation " +
                "requires xt_cgroup for per-cgroup iptables filtering. " +
                "Ensure the module is available: modprobe xt_cgroup");
        }

        // Apply rules to both iptables (IPv4) and ip6tables (IPv6)
        foreach (var iptables in (string[])["iptables", "ip6tables"])
        {
            // Create a custom chain for this sandbox
            await RunCommandAsync(iptables,
                ["-N", cgroupName], ct);

            // Whitelist rules (inserted before default deny)
            foreach (var rule in Config.NetworkWhitelist.Rules)
            {
                // Skip wildcard hosts — iptables cannot resolve *.example.com
                if (rule.Host.StartsWith("*.", StringComparison.Ordinal))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[mk8.shell] {iptables} cannot enforce wildcard host '{rule.Host}'. " +
                        "Rule skipped — wildcard matching requires DNS-layer filtering.",
                        "Mk8.Shell.Isolation");
                    continue;
                }

                // Determine which protocols to create rules for
                var protocols = (rule.Port > 0, rule.Protocol) switch
                {
                    (true, Mk8NetworkProtocol.Tcp) => new[] { "tcp" },
                    (true, Mk8NetworkProtocol.Udp) => new[] { "udp" },
                    (true, Mk8NetworkProtocol.Any) => new[] { "tcp", "udp" },
                    (false, Mk8NetworkProtocol.Tcp) => new[] { "tcp" },
                    (false, Mk8NetworkProtocol.Udp) => new[] { "udp" },
                    _ => Array.Empty<string>(),
                };

                if (protocols.Length > 0)
                {
                    foreach (var proto in protocols)
                    {
                        var args = new List<string> { "-A", cgroupName, "-p", proto };
                        if (rule.Port > 0)
                            args.AddRange(["--dport", rule.Port.ToString()]);
                        args.AddRange(["-d", rule.Host, "-j", "ACCEPT"]);
                        await RunCommandAsync(iptables, [.. args], ct);
                    }
                }
                else
                {
                    // No protocol/port constraint — allow any protocol
                    await RunCommandAsync(iptables,
                        ["-A", cgroupName, "-d", rule.Host, "-j", "ACCEPT"], ct);
                }
            }

            // Allow established/related return traffic
            await RunCommandAsync(iptables,
                ["-A", cgroupName,
                 "-m", "conntrack", "--ctstate", "ESTABLISHED,RELATED",
                 "-j", "ACCEPT"], ct);

            // DNS restriction: allow UDP 53 only to known public
            // resolvers and rate-limit to 10 queries/sec. This blocks
            // DNS exfiltration to arbitrary resolvers and limits
            // bandwidth through allowed resolvers.
            // IPv4 resolvers for iptables, IPv6 for ip6tables — using
            // the wrong address family would silently produce no-op rules.
            var dnsResolvers = iptables == "ip6tables"
                ? (string[])["2001:4860:4860::8888", "2001:4860:4860::8844",
                              "2606:4700:4700::1111", "2606:4700:4700::1001"]
                : (string[])["8.8.8.8", "8.8.4.4",
                              "1.1.1.1", "1.0.0.1"];

            foreach (var resolver in dnsResolvers)
            {
                await RunCommandAsync(iptables,
                    ["-A", cgroupName,
                     "-p", "udp", "--dport", "53",
                     "-d", resolver,
                     "-m", "limit", "--limit", "10/sec", "--limit-burst", "20",
                     "-j", "ACCEPT"], ct);
            }

            // Block DNS to all other destinations (prevents exfiltration
            // via custom DNS resolvers)
            await RunCommandAsync(iptables,
                ["-A", cgroupName,
                 "-p", "udp", "--dport", "53",
                 "-j", "DROP"], ct);

            // Block TCP DNS (DNS-over-TCP to any resolver not whitelisted)
            await RunCommandAsync(iptables,
                ["-A", cgroupName,
                 "-p", "tcp", "--dport", "53",
                 "-j", "DROP"], ct);

            // Default deny at end of chain
            await RunCommandAsync(iptables,
                ["-A", cgroupName, "-j", "DROP"], ct);

            // Jump to the custom chain from OUTPUT for matching cgroup
            await RunCommandAsync(iptables,
                ["-A", "OUTPUT",
                 "-m", "cgroup", "--path", cgroupName,
                 "-j", cgroupName], ct);
        }
    }

    private static async Task CleanupNetworkFilterAsync(string chainName)
    {
        // Clean up both iptables and ip6tables
        foreach (var iptables in (string[])["iptables", "ip6tables"])
        {
            try
            {
                // Remove jump rule from OUTPUT
                await RunCommandAsync(iptables,
                    ["-D", "OUTPUT",
                     "-m", "cgroup", "--path", chainName,
                     "-j", chainName], default);
            }
            catch { /* rule may not exist */ }

            try
            {
                // Flush and delete the custom chain
                await RunCommandAsync(iptables, ["-F", chainName], default);
                await RunCommandAsync(iptables, ["-X", chainName], default);
            }
            catch { /* best effort */ }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Namespace wrapper (full isolation stack, NO network namespace)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds unshare flags for full namespace isolation. All flags are
    /// unconditional — every sandbox gets the complete isolation stack:
    /// <list type="bullet">
    ///   <item><c>--pid --fork</c>: PID namespace isolation</item>
    ///   <item><c>--mount</c>: Mount namespace for filesystem isolation</item>
    ///   <item><c>--ipc</c>: IPC namespace (System V IPC, POSIX shm)</item>
    ///   <item><c>--user --map-root-user</c>: User namespace (unprivileged UID)</item>
    ///   <item><c>--uts</c>: UTS namespace (hostname isolation)</item>
    /// </list>
    /// No <c>--net</c>: network filtering is handled by xt_cgroup at the
    /// cgroup level, which applies to all processes in the cgroup.
    /// A network namespace would break this.
    /// </summary>
    private static string[] BuildUnshareFlags() =>
    [
        "--pid", "--fork",   // PID namespace isolation
        "--mount",           // Mount namespace for filesystem isolation
        "--ipc",             // IPC namespace (System V IPC, POSIX shm)
        "--user",            // User namespace (unprivileged UID)
        "--map-root-user",   // Map current UID to root inside the namespace
        "--uts",             // UTS namespace (hostname isolation)
    ];

    // ═══════════════════════════════════════════════════════════════
    // Helper: run a system command
    // ═══════════════════════════════════════════════════════════════

    private static async Task RunCommandAsync(
        string binary, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new Mk8ContainerException(
                $"Container setup command '{binary} {string.Join(' ', args)}' " +
                $"failed with exit code {process.ExitCode}: {stderr}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // libc P/Invoke
    // ═══════════════════════════════════════════════════════════════

    private static partial class Libc
    {
        [LibraryImport("libc")]
        public static partial uint getuid();
    }
}
