using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered external information store (remote API, cloud vector DB,
/// etc.) that agents can be granted access to.
/// </summary>
public class ExternalInformationStoreDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>Remote endpoint URL or connection string.</summary>
    public required string Endpoint { get; set; }

    public string? Description { get; set; }

    /// <summary>Encrypted API key / credential for the external store.</summary>
    public string? EncryptedApiKey { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    public ICollection<ExternalInfoStoreAccessDB> Permissions { get; set; } = [];
}
