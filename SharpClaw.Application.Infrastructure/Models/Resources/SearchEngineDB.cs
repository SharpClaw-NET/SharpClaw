using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered search engine that agents can query through the
/// .NET service layer.  The <see cref="Type"/> determines which
/// API schema / parameter mapping is used at execution time.
/// </summary>
public class SearchEngineDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// Provider type — determines query parameter mapping, authentication
    /// scheme, and response parsing.
    /// </summary>
    public SearchEngineType Type { get; set; } = SearchEngineType.Custom;

    /// <summary>Search API endpoint URL.</summary>
    public required string Endpoint { get; set; }

    public string? Description { get; set; }

    /// <summary>Encrypted API key for the search service.</summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>
    /// Optional secondary credential (e.g. Google Custom Search Engine ID,
    /// Yandex folder ID, Baidu secret key).
    /// </summary>
    public string? EncryptedSecondaryKey { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    }
