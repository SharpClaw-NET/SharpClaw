using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered website that agents can be granted access to via the
/// .NET HTTP service layer (not through a terminal browser).
/// </summary>
public class WebsiteDB : BaseEntity
{
    public required string Name { get; set; }

    public required string Url { get; set; }

    public string? Description { get; set; }

    /// <summary>Encrypted credentials (password, token, cookie, etc.) for authentication.</summary>
    public string? EncryptedCredentials { get; set; }

    /// <summary>Optional login endpoint when authentication is form-based or OAuth.</summary>
    public string? LoginUrl { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    public ICollection<WebsiteAccessDB> Accesses { get; set; } = [];
}
