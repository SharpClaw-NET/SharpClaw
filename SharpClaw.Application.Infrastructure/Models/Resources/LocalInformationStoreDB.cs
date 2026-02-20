using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered local information store (filesystem-based knowledge base,
/// vector DB, etc.) that agents can be granted access to.
/// </summary>
public class LocalInformationStoreDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>Filesystem path or connection detail for the local store.</summary>
    public required string Path { get; set; }

    public string? Description { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    public ICollection<LocalInfoStoreAccessDB> Permissions { get; set; } = [];
}
