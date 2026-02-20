using SharpClaw.Application.Infrastructure.Models.Access;
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

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    public ICollection<SystemUserAccessDB> Accesses { get; set; } = [];
}
