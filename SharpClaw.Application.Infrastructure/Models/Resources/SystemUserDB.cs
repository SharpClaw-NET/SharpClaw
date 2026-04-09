using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered operating-system user account that agents can execute
/// terminal commands as.
/// </summary>
public class SystemUserDB : BaseEntity
{
    /// <summary>OS username (e.g. "deploy", "www-data").</summary>
    public required string Username { get; set; }

    public string? Description { get; set; }

    // ── mk8.shell workspace ───────────────────────────────────────

    /// <summary>
    /// Absolute path to the sandbox root directory. All file/dir
    /// operations are jailed inside this path.
    /// Defaults to the user's home directory when <c>null</c>.
    /// </summary>
    public string? SandboxRoot { get; set; }

    /// <summary>
    /// Initial working directory for process execution. Must be
    /// inside <see cref="SandboxRoot"/>. Defaults to SandboxRoot.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Default per-step timeout in seconds for mk8.shell scripts
    /// run as this user. Overridable per-script.
    /// </summary>
    public int DefaultStepTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Default script-level max retries. Overridable per-script and
    /// per-step.
    /// </summary>
    public int DefaultMaxRetries { get; set; }

    // ── Relations ─────────────────────────────────────────────────

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    }
