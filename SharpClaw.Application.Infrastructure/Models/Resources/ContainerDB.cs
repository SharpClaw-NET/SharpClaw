using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered sandbox, VM, or container environment for isolated
/// code execution.
/// </summary>
public class ContainerDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>Container image or environment identifier.</summary>
    public string? Image { get; set; }

    /// <summary>Runtime endpoint for the container orchestrator.</summary>
    public string? Endpoint { get; set; }

    public string? Description { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    public ICollection<ContainerAccessDB> Accesses { get; set; } = [];
}
