using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts.Attributes;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Infrastructure.Models;

public class UserDB : BaseEntity
{
    public required string Username { get; set; }

    [HeaderSensitive]
    public required byte[] PasswordHash { get; set; }

    [HeaderSensitive]
    public required byte[] PasswordSalt { get; set; }

    /// <summary>
    /// Marks the user as an administrator who can list users and
    /// assign roles to other users.
    /// </summary>
    public bool IsUserAdmin { get; set; }

    /// <summary>
    /// Free-form user bio included in chat headers so agents know
    /// who they are talking to.
    /// </summary>
    public string? Bio { get; set; }

    /// <summary>
    /// Any access token issued before this timestamp is considered invalid.
    /// Set to <see cref="DateTimeOffset.UtcNow"/> to invalidate all current access tokens.
    /// </summary>
    public DateTimeOffset AccessTokensInvalidatedAt { get; set; }

    public Guid? RoleId { get; set; }
    public RoleDB? Role { get; set; }

    public ICollection<RefreshTokenDB> RefreshTokens { get; set; } = [];
}
