using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered search engine that agents can query through the
/// .NET service layer.
/// </summary>
public class SearchEngineDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>Search API endpoint URL.</summary>
    public required string Endpoint { get; set; }

    public string? Description { get; set; }

    /// <summary>Encrypted API key for the search service.</summary>
    public string? EncryptedApiKey { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    public ICollection<SearchEngineAccessDB> Accesses { get; set; } = [];
}
