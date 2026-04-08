namespace SharpClaw.Contracts.DTOs.Websites;

/// <summary>Request to create a new website resource.</summary>
public sealed record CreateWebsiteRequest(
    string Name,
    string Url,
    string? Description = null,
    string? LoginUrl = null,
    string? Credentials = null,
    Guid? SkillId = null);

/// <summary>Request to update an existing website resource. Null fields are left unchanged.</summary>
public sealed record UpdateWebsiteRequest(
    string? Name = null,
    string? Url = null,
    string? Description = null,
    string? LoginUrl = null,
    string? Credentials = null,
    Guid? SkillId = null);

/// <summary>Read-only projection of a website resource.</summary>
public sealed record WebsiteResponse(
    Guid Id,
    string Name,
    string Url,
    string? Description,
    string? LoginUrl,
    bool HasCredentials,
    Guid? SkillId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
