using System.Diagnostics;

namespace Mk8.Shell.Isolation;

/// <summary>
/// Result of launching a process inside a sandbox container. The
/// container owns OS resource cleanup — individual processes only
/// need their <see cref="Process"/> handle disposed.
/// </summary>
public sealed class Mk8ContainedProcess : IAsyncDisposable
{
    /// <summary>The spawned process.</summary>
    public required Process Process { get; init; }

    /// <summary>
    /// Platform-specific container identifier for diagnostics.
    /// Linux (bare-metal): cgroup path. Windows (WSL2): cgroup path
    /// inside the WSL2 distro.
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// Waits for the contained process to exit.
    /// </summary>
    public Task WaitForExitAsync(CancellationToken ct = default) =>
        Process.WaitForExitAsync(ct);

    public ValueTask DisposeAsync()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }

        Process.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Abstract base for per-sandbox persistent OS containers. Each sandbox
/// gets one container that lives from sandbox registration until
/// unregistration (or mk8.shell process shutdown).
/// Container isolation is mandatory for all mk8.shell sandboxes —
/// macOS is not supported and will throw <see cref="PlatformNotSupportedException"/>.
/// <para>
/// Two launch paths share a single isolation backend:
/// <list type="bullet">
///   <item><b>Linux (bare-metal):</b> <see cref="Mk8LinuxSandboxContainer"/>
///     runs directly — cgroups v2, kernel namespace isolation (PID, mount,
///     IPC, user, UTS), xt_cgroup iptables/ip6tables.</item>
///   <item><b>Windows (WSL2):</b> <see cref="Mk8Wsl2SandboxContainer"/> runs
///     sandbox processes inside a dedicated WSL2 distro via direct
///     <c>wsl.exe</c> invocations. Inside the distro, processes get the
///     exact same isolation as bare-metal Linux — cgroups v2, kernel
///     namespace isolation, and xt_cgroup network filtering.
///     One codebase to audit.</item>
/// </list>
/// </para>
/// <para>
/// The container provides:
/// <list type="bullet">
///   <item><b>Process containment:</b> All processes launched via
///     <see cref="LaunchAsync"/> are assigned to the container's cgroup
///     immediately at launch time.</item>
///   <item><b>Resource limits:</b> Memory, CPU, process count, and write
///     byte limits applied to the entire container.</item>
///   <item><b>Network iron curtain:</b> All outbound traffic blocked
///     by default; only whitelisted destinations permitted.</item>
///   <item><b>Full isolation:</b> Filesystem, PID, IPC, user, and network
///     isolation — sandbox processes cannot see, signal, read from, write
///     to, or communicate with anything on the host beyond explicitly
///     mapped directories.</item>
/// </list>
/// </para>
/// <para>
/// The container does NOT replace mk8.shell's existing safety layers
/// (path sandboxing, binary allowlist, gigablacklist, etc.). It adds
/// an OS-enforced boundary beneath them — defense in depth.
/// </para>
/// </summary>
public abstract class Mk8SandboxContainer : IAsyncDisposable
{
    /// <summary>The active configuration for this container.</summary>
    protected Mk8ContainerConfig Config { get; }

    /// <summary>Absolute path to the sandbox root directory.</summary>
    protected string SandboxPath { get; }

    /// <summary>Sandbox identifier for naming OS resources.</summary>
    protected string SandboxId { get; }

    /// <summary>Whether the container is currently active.</summary>
    public bool IsActive { get; protected set; }

    /// <summary>
    /// Total bytes written by all processes in this container since
    /// <see cref="StartAsync"/>. Tracked via OS-level counters (cgroup
    /// <c>io.stat</c>) on both bare-metal Linux and WSL2.
    /// </summary>
    public long WriteBytesUsed { get; protected set; }

    protected Mk8SandboxContainer(
        Mk8ContainerConfig config, string sandboxPath, string sandboxId)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        SandboxPath = sandboxPath ?? throw new ArgumentNullException(nameof(sandboxPath));
        SandboxId = sandboxId ?? throw new ArgumentNullException(nameof(sandboxId));
    }

    /// <summary>
    /// Creates persistent OS resources (cgroup, network filters,
    /// write byte tracker) and prepares the container for process launches.
    /// Must be called before <see cref="LaunchAsync"/>.
    /// </summary>
    public abstract Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Launches a process inside the already-active container.
    /// The process is assigned to the container's OS boundary
    /// (cgroup + namespaces) immediately.
    /// </summary>
    public abstract Task<Mk8ContainedProcess> LaunchAsync(
        ProcessStartInfo startInfo,
        CancellationToken ct = default);

    /// <summary>
    /// Tears down the container: stops the write byte tracker, kills all
    /// contained processes, and removes OS resources (cgroup, network
    /// filters).
    /// </summary>
    public abstract Task StopAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Checks whether the current environment supports container isolation.
    /// </summary>
    public abstract Mk8ContainerCapability CheckCapability();

    /// <summary>
    /// Creates the appropriate platform container and validates that
    /// critical isolation capabilities are available. Container isolation
    /// is mandatory — this never returns <c>null</c>. Throws
    /// <see cref="PlatformNotSupportedException"/> on macOS, and
    /// <see cref="Mk8ContainerException"/> if the platform cannot
    /// provide required isolation properties.
    /// <para>
    /// On Linux, creates a cgroups v2 + namespace container directly.
    /// On Windows, creates a WSL2-backed container that runs the same
    /// Linux isolation code inside a dedicated WSL2 distro. There is
    /// no fallback — if WSL2 is unavailable, this throws.
    /// </para>
    /// </summary>
    public static Mk8SandboxContainer Create(
        Mk8ContainerConfig config, string sandboxPath, string sandboxId)
    {
        Mk8SandboxContainer container;

        if (OperatingSystem.IsLinux())
        {
            container = new Mk8LinuxSandboxContainer(config, sandboxPath, sandboxId);
        }
        else if (OperatingSystem.IsWindows())
        {
            if (!Mk8Wsl2SandboxContainer.IsAvailable())
            {
                throw new Mk8ContainerException(
                    "WSL2 container isolation is required on Windows but " +
                    "WSL2 is unavailable. Ensure that WSL2 is installed " +
                    "(wsl --install) and a Linux kernel is present. " +
                    "mk8.shell cannot operate without full VM-style " +
                    "isolation.");
            }

            container = new Mk8Wsl2SandboxContainer(config, sandboxPath, sandboxId);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "mk8.shell execution is not supported on macOS. " +
                "Container isolation requires Linux (cgroups v2, namespaces, " +
                "xt_cgroup) or Windows with WSL2. macOS lacks the kernel " +
                "primitives needed for mandatory process containment.");
        }

        // Validate that critical isolation properties are available.
        // The container MUST provide filesystem, process, IPC, and
        // network isolation. If any are missing, refuse to proceed.
        var caps = container.CheckCapability();
        EnforceCapabilities(caps);

        return container;
    }

    /// <summary>
    /// Validates that the container provides all critical isolation
    /// properties. Throws <see cref="Mk8ContainerException"/> with a
    /// detailed message if any required capability is missing.
    /// </summary>
    private static void EnforceCapabilities(Mk8ContainerCapability caps)
    {
        if (!caps.Supported)
            throw new Mk8ContainerException(
                $"Container isolation is not supported on this system. {caps.Notes}");

        var missing = new List<string>();

        if (!caps.FilesystemIsolation)
            missing.Add("filesystem isolation");
        if (!caps.ProcessIsolation)
            missing.Add("process (PID) isolation");
        if (!caps.IpcIsolation)
            missing.Add("IPC isolation");
        if (!caps.NetworkFiltering)
            missing.Add("network filtering");

        if (missing.Count > 0)
        {
            throw new Mk8ContainerException(
                $"Container is missing required isolation capabilities: " +
                $"{string.Join(", ", missing)}. " +
                $"mk8.shell requires full VM-style isolation and cannot " +
                $"operate in degraded mode. {caps.Notes}");
        }
    }

    protected static string SanitizeName(string name) =>
        new(name.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
}

/// <summary>
/// Describes the platform's container isolation capabilities.
/// All boolean properties reflect actual runtime capabilities — not
/// aspirational claims. The factory (<see cref="Mk8SandboxContainer.Create"/>)
/// enforces that critical capabilities are <c>true</c> before allowing
/// sandbox execution.
/// </summary>
public sealed class Mk8ContainerCapability
{
    /// <summary>Whether the platform supports any isolation at all.</summary>
    public required bool Supported { get; init; }

    /// <summary>
    /// Whether PID namespace isolation is available. Processes inside
    /// the sandbox cannot see or signal host processes.
    /// </summary>
    public required bool ProcessIsolation { get; init; }

    /// <summary>Whether resource limits (memory, CPU, PIDs) are available.</summary>
    public required bool ResourceLimits { get; init; }

    /// <summary>
    /// Whether rule-based network filtering is available. This means
    /// granular per-destination PERMIT rules — not just total network
    /// cutoff. Total cutoff without whitelisting does NOT qualify.
    /// </summary>
    public required bool NetworkFiltering { get; init; }

    /// <summary>
    /// Whether filesystem isolation is available. Processes inside the
    /// sandbox can only see the sandbox directory (read-write) and
    /// explicitly mapped tool directories (read-only). Everything else
    /// on the host filesystem is invisible.
    /// </summary>
    public required bool FilesystemIsolation { get; init; }

    /// <summary>
    /// Whether IPC isolation is available. Sandbox processes cannot
    /// communicate with host processes via shared memory, named pipes,
    /// semaphores, Unix domain sockets, or any other IPC mechanism.
    /// </summary>
    public required bool IpcIsolation { get; init; }

    /// <summary>Whether write byte tracking is available.</summary>
    public required bool WriteByteLimits { get; init; }

    /// <summary>
    /// Human-readable notes about missing capabilities or required
    /// prerequisites.
    /// </summary>
    public string? Notes { get; init; }
}
