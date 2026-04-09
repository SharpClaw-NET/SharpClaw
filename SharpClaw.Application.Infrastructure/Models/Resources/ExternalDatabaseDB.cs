using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered external database that agents can be granted access to.
/// The model provides queries appropriate to the <see cref="DatabaseType"/>
/// (e.g. SQL for MySQL/PostgreSQL/MSSQL, MongoDB query JSON for MongoDB, etc.).
/// </summary>
public class ExternalDatabaseDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>Database engine type (MySQL, PostgreSQL, etc.).</summary>
    public DatabaseType DatabaseType { get; set; }

    /// <summary>Encrypted connection string for the external database.</summary>
    public required string EncryptedConnectionString { get; set; }

    public string? Description { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    }
