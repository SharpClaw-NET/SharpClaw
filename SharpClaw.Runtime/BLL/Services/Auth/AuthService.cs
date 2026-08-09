using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.Auth;
using SharpClaw.Contracts.DTOs.Users;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Security;
using SharpClaw.Runtime.BLL.Services;

namespace SharpClaw.Runtime.BLL.Services.Auth;

public sealed class AuthService(
    SharpClawDbContext db,
    TokenService tokenService,
    JwtOptions jwtOptions,
    ChatCache chatCache,
    IConfiguration configuration)
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
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.AccessTokensInvalidatedAt, now),
                ct);
    }

    /// <summary>
    /// Revokes all refresh tokens for the specified users.
    /// Existing access tokens remain valid until they expire naturally.
    /// </summary>
    public async Task InvalidateRefreshTokensAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        await db.RefreshTokens
            .Where(r => userIds.Contains(r.UserId) && !r.IsRevoked)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.IsRevoked, true),
                ct);
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

    public async Task<Guid?> ResolveDisabledAuthSessionUserIdAsync(
        CancellationToken ct = default)
    {
        var anonymousUsername = configuration["Auth:AnonymousUsername"];
        if (anonymousUsername is not null)
        {
            if (string.IsNullOrWhiteSpace(anonymousUsername))
            {
                throw new InvalidOperationException(
                    "Auth:AnonymousUsername is configured but empty.");
            }

            return await db.Users
                .Where(user => user.Username == anonymousUsername)
                .Select(user => (Guid?)user.Id)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    $"Auth:AnonymousUsername is set to '{anonymousUsername}', but no matching user exists.");
        }

        var configuredUsername = configuration["Admin:Username"];
        if (!string.IsNullOrWhiteSpace(configuredUsername))
        {
            var configuredAdminId = await db.Users
                .Where(user => user.Username == configuredUsername && user.IsUserAdmin)
                .Select(user => (Guid?)user.Id)
                .FirstOrDefaultAsync(ct);

            if (configuredAdminId is not null)
                return configuredAdminId;
        }

        return await db.Users
            .Where(user => user.IsUserAdmin)
            .OrderBy(user => user.Username)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Returns the profile of the user with the given ID, or <c>null</c> if not found.
    /// </summary>
    public async Task<MeResponse?> GetMeAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return null;

        var roleId = ReadRoleId(user);
        var role = roleId is { } id
            ? await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct)
            : null;

        return new MeResponse(
            user.Id,
            user.Username,
            user.Bio,
            roleId,
            role?.Name,
            user.IsUserAdmin);
    }

    // ═══════════════════════════════════════════════════════════════
    // User administration (requires IsUserAdmin)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Lists all registered users. Only user admins may call this.
    /// </summary>
    public async Task<IReadOnlyList<UserEntry>> ListUsersAsync(
        Guid callerUserId, CancellationToken ct = default)
    {
        await EnsureUserAdminAsync(callerUserId, ct);

        var users = await db.Users
            .OrderBy(u => u.Username)
            .ToListAsync(ct);

        var roleIds = users
            .Select(ReadRoleId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToArray();
        var roles = await db.Roles
            .Where(role => roleIds.Contains(role.Id))
            .ToDictionaryAsync(role => role.Id, ct);

        return users
            .Select(user =>
            {
                var roleId = ReadRoleId(user);
                return new UserEntry(
                    user.Id,
                    user.Username,
                    user.Bio,
                    roleId,
                    roleId is { } id && roles.TryGetValue(id, out var role)
                        ? role.Name
                        : null,
                    user.IsUserAdmin);
            })
            .ToList();
    }

    /// <summary>
    /// Assigns (or removes) a role on another user. Only user admins may call this.
    /// Pass <see cref="Guid.Empty"/> as <paramref name="roleId"/> to remove the role.
    /// </summary>
    public async Task<UserEntry?> SetUserRoleAsync(
        Guid targetUserId, Guid roleId, Guid callerUserId, CancellationToken ct = default)
    {
        await EnsureUserAdminAsync(callerUserId, ct);

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (user is null) return null;

        RoleDB? role = null;
        if (roleId == Guid.Empty)
        {
            db.Entry(user).Property<Guid?>("RoleId").CurrentValue = null;
        }
        else
        {
            role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
                ?? throw new ArgumentException($"Role {roleId} not found.");
            db.Entry(user).Property<Guid?>("RoleId").CurrentValue = role.Id;
        }

        await db.SaveChangesAsync(ct);
        chatCache.Remove(ChatCache.KeyHeaderUser(targetUserId));

        var assignedRoleId = role?.Id;

        return new UserEntry(
            user.Id, user.Username, user.Bio,
            assignedRoleId, role?.Name, user.IsUserAdmin);
    }

    private async Task EnsureUserAdminAsync(Guid userId, CancellationToken ct)
    {
        var caller = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (caller is null || !caller.IsUserAdmin)
            throw new UnauthorizedAccessException("Only user admins can perform this action.");
    }

    private Guid? ReadRoleId(UserDB user) =>
        db.Entry(user).Property<Guid?>("RoleId").CurrentValue;
}
