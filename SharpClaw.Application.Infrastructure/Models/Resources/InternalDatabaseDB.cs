using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered internal database managed by SharpClaw itself.
/// Not yet supported — this entity exists for future use.
/// </summary>
public class InternalDatabaseDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>Database engine type (MySQL, PostgreSQL, etc.).</summary>
    public DatabaseType DatabaseType { get; set; }

    /// <summary>Filesystem path or connection detail for the internal database.</summary>
    public required string Path { get; set; }

    public string? Description { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    }
