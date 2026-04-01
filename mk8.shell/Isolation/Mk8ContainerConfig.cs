namespace Mk8.Shell.Isolation;

/// <summary>
/// A directory mapping from the host filesystem into the container.
/// The sandbox directory is always mapped read-write implicitly —
/// callers should not include it here.
/// </summary>
/// <param name="HostPath">Absolute path on the host filesystem.</param>
/// <param name="ContainerPath">Path inside the container.</param>
/// <param name="ReadOnly">
/// When <c>true</c>, the directory is mounted read-only inside the
/// container. Tool directories (dotnet, git, node, etc.) should
/// always be read-only.
/// </param>
public sealed record MappedDirectory(
    string HostPath,
    string ContainerPath,
    bool ReadOnly);

/// <summary>
/// Platform-agnostic configuration for OS-level container isolation
/// applied to mk8.shell sandbox processes. Expresses WHAT isolation
/// the caller wants — both launch paths (direct Linux, WSL2-wrapped
/// on Windows) implement the same contract via
/// <see cref="Mk8LinuxSandboxContainer"/>.
/// <para>
/// Container isolation is mandatory and total for all mk8.shell
/// sandboxes. Every sandbox gets full VM-style isolation: filesystem,
/// PID, IPC, network, user, and resource limits. macOS is not
/// supported — mk8.shell execution is denied on macOS.
/// </para>
/// <para>
/// Configured in base.env (global defaults) and optionally overridden
/// per-sandbox via signed env. Sandbox overrides can only tighten
/// limits, never loosen them — the effective value is always the more
/// restrictive of global vs sandbox.
/// </para>
/// </summary>
public sealed class Mk8ContainerConfig
{

    // ── Resource limits ───────────────────────────────────────────

    /// <summary>
    /// Maximum memory in bytes. Enforced at the kernel level via
    /// cgroups <c>memory.max</c> on both bare-metal Linux and WSL2.
    /// <c>0</c> = unlimited (OS default).
    /// <para>Example: 536_870_912 (512 MB)</para>
    /// </summary>
    public long MemoryLimitBytes { get; init; }

    /// <summary>
    /// CPU quota as a percentage of one core. <c>100</c> = one full
    /// core. <c>50</c> = half a core. <c>0</c> = unlimited.
    /// </summary>
    public int CpuPercentLimit { get; init; }

    /// <summary>
    /// Maximum number of processes (including the root process) the
    /// sandbox may spawn. <c>0</c> = unlimited.
    /// <para>Default: 32</para>
    /// </summary>
    public int MaxProcesses { get; init; } = 32;

    /// <summary>
    /// Maximum total file write bytes during a single execution.
    /// When the limit is breached, all processes in the container
    /// are killed. <c>0</c> = unlimited.
    /// <para>Default: 0 (unlimited)</para>
    /// </summary>
    public long MaxWriteBytes { get; init; }

    // ── Network policy ────────────────────────────────────────────

    /// <summary>
    /// Network whitelist. ALL outbound traffic is blocked by default
    /// (iron curtain). Only destinations listed here are permitted.
    /// Enforcement is at the IP:port:protocol layer via
    /// <c>xt_cgroup</c> iptables + ip6tables on both bare-metal
    /// Linux and WSL2.
    /// </summary>
    public Mk8NetworkWhitelist NetworkWhitelist { get; init; } = new();

    // ── Mapped directories ────────────────────────────────────────

    /// <summary>
    /// Explicit list of host directories mapped into the container.
    /// The sandbox directory is always mapped read-write implicitly
    /// and must NOT be included here. Tool directories (dotnet, git,
    /// node, etc.) should be listed as read-only mappings.
    /// <para>
    /// Nothing outside these mappings and the sandbox directory is
    /// visible inside the container. Enforced via mount namespace
    /// bind mounts on both bare-metal Linux and WSL2.
    /// </para>
    /// </summary>
    public IReadOnlyList<MappedDirectory> MappedDirectories { get; init; } = [];

    /// <summary>
    /// Creates a sensible default config with standard resource limits.
    /// </summary>
    public static Mk8ContainerConfig Default => new()
    {
        MemoryLimitBytes = 512 * 1024 * 1024, // 512 MB
        CpuPercentLimit = 100,                 // 1 core
        MaxProcesses = 32,
    };

    /// <summary>
    /// Applies the "more restrictive wins" merge for sandbox overrides.
    /// Mapped directories are infrastructure-level — sandbox cannot
    /// override them.
    /// </summary>
    public Mk8ContainerConfig TightenWith(Mk8ContainerConfig? sandbox)
    {
        if (sandbox is null)
            return this;

        return new Mk8ContainerConfig
        {
            MemoryLimitBytes = TightenLong(MemoryLimitBytes, sandbox.MemoryLimitBytes),
            CpuPercentLimit = TightenInt(CpuPercentLimit, sandbox.CpuPercentLimit),
            MaxProcesses = TightenInt(MaxProcesses, sandbox.MaxProcesses),
            MaxWriteBytes = TightenLong(MaxWriteBytes, sandbox.MaxWriteBytes),
            NetworkWhitelist = NetworkWhitelist.MergeWith(sandbox.NetworkWhitelist),
            // Mapped directories are infrastructure-level — not sandbox-overridable
            MappedDirectories = MappedDirectories,
        };
    }

    /// <summary>
    /// For limits where 0 = unlimited: if either is non-zero, pick the
    /// smaller non-zero value.
    /// </summary>
    private static long TightenLong(long a, long b) =>
        (a, b) switch
        {
            (0, _) => b,
            (_, 0) => a,
            _      => Math.Min(a, b),
        };

    private static int TightenInt(int a, int b) =>
        (a, b) switch
        {
            (0, _) => b,
            (_, 0) => a,
            _      => Math.Min(a, b),
        };
}
