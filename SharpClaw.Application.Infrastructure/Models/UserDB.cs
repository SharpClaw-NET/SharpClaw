using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Infrastructure.Models;

public class UserDB : BaseEntity
{
    public required string Username { get; set; }
    public required byte[] PasswordHash { get; set; }
    public required byte[] PasswordSalt { get; set; }

    /// <summary>
    /// Any access token issued before this timestamp is considered invalid.
    /// Set to <see cref="DateTimeOffset.UtcNow"/> to invalidate all current access tokens.
    /// </summary>
    public DateTimeOffset AccessTokensInvalidatedAt { get; set; }

    public Guid? RoleId { get; set; }
    public RoleDB? Role { get; set; }

    public ICollection<RefreshTokenDB> RefreshTokens { get; set; } = [];
}
