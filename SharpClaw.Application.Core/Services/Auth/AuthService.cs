using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.Auth;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services.Auth;

public sealed class AuthService(
    SharpClawDbContext db,
    TokenService tokenService,
    JwtOptions jwtOptions)
{
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Username == request.Username, ct);

        if (user is null || !PasswordHelper.Verify(request.Password, user.PasswordHash, user.PasswordSalt))
            return null;

        var accessToken = tokenService.GenerateAccessToken(user.Id, user.Username);
        var accessExpiresAt = DateTimeOffset.UtcNow.Add(jwtOptions.AccessTokenLifetime);

        string? refreshToken = null;
        DateTimeOffset? refreshExpiresAt = null;

        if (request.RememberMe)
        {
            refreshToken = TokenService.GenerateRefreshToken();
            refreshExpiresAt = DateTimeOffset.UtcNow.Add(jwtOptions.RefreshTokenLifetime);

            db.RefreshTokens.Add(new RefreshTokenDB
            {
                Token = refreshToken,
                ExpiresAt = refreshExpiresAt.Value,
                UserId = user.Id
            });

            await db.SaveChangesAsync(ct);
        }

        return new LoginResponse(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt);
    }

    public async Task<LoginResponse?> RefreshAsync(RefreshRequest request, CancellationToken ct = default)
    {
        var stored = await db.RefreshTokens
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Token == request.RefreshToken, ct);

        if (stored is null || stored.IsRevoked || stored.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;

        // Rotate: revoke the old refresh token and issue a new one
        stored.IsRevoked = true;

        var newRefreshToken = TokenService.GenerateRefreshToken();
        var refreshExpiresAt = DateTimeOffset.UtcNow.Add(jwtOptions.RefreshTokenLifetime);

        db.RefreshTokens.Add(new RefreshTokenDB
        {
            Token = newRefreshToken,
            ExpiresAt = refreshExpiresAt,
            UserId = stored.UserId
        });

        await db.SaveChangesAsync(ct);

        var accessToken = tokenService.GenerateAccessToken(stored.UserId, stored.User.Username);
        var accessExpiresAt = DateTimeOffset.UtcNow.Add(jwtOptions.AccessTokenLifetime);

        return new LoginResponse(accessToken, accessExpiresAt, newRefreshToken, refreshExpiresAt);
    }

    /// <summary>
    /// Invalidates all currently issued access tokens for the specified users.
    /// Refresh tokens remain valid. New access tokens issued after this call will work.
    /// </summary>
    public async Task InvalidateAccessTokensAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        await db.Users
            .Where(u => userIds.Contains(u.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessTokensInvalidatedAt, now), ct);
    }

    /// <summary>
    /// Revokes all refresh tokens for the specified users.
    /// Existing access tokens remain valid until they expire naturally.
    /// </summary>
    public async Task InvalidateRefreshTokensAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        await db.RefreshTokens
            .Where(r => userIds.Contains(r.UserId) && !r.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRevoked, true), ct);
    }

    /// <summary>
    /// Checks whether an access token's issued-at time is still valid for the given user.
    /// Returns false if the token was issued before the user's invalidation timestamp.
    /// </summary>
    public async Task<bool> IsAccessTokenValidForUserAsync(
        Guid userId, DateTimeOffset issuedAt, CancellationToken ct = default)
    {
        var invalidatedAt = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.AccessTokensInvalidatedAt)
            .FirstOrDefaultAsync(ct);

        return issuedAt > invalidatedAt;
    }

    public async Task<UserDB> RegisterAsync(string username, string password, CancellationToken ct = default)
    {
        var salt = PasswordHelper.GenerateSalt();
        var hash = PasswordHelper.Hash(password, salt);

        var user = new UserDB
        {
            Username = username,
            PasswordHash = hash,
            PasswordSalt = salt
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>
    /// Returns the profile of the user with the given ID, or <c>null</c> if not found.
    /// </summary>
    public async Task<MeResponse?> GetMeAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return null;

        return new MeResponse(
            user.Id,
            user.Username,
            user.Bio,
            user.RoleId,
            user.Role?.Name);
    }
}
