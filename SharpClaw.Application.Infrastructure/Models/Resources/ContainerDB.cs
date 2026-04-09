using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered sandbox, VM, or container environment for isolated
/// code execution.
/// </summary>
public class ContainerDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// The type of container. Determines registration and lifecycle
    /// semantics.
    /// </summary>
    public ContainerType Type { get; set; } = ContainerType.Mk8Shell;

    /// <summary>
    /// For <see cref="ContainerType.Mk8Shell"/>: the sandbox name
    /// registered via mk8.shell.startup. This is the only identifier
    /// stored — the local path is resolved at runtime from
    /// <c>%APPDATA%/mk8.shell/sandboxes.json</c>.
    /// </summary>
    public string? SandboxName { get; set; }

    /// <summary>Container image or environment identifier.</summary>
    public string? Image { get; set; }

    /// <summary>Runtime endpoint for the container orchestrator.</summary>
    public string? Endpoint { get; set; }

    public string? Description { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    }
