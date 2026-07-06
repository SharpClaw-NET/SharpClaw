using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SharpClaw.Runtime.BLL.Services.Auth;

public sealed class TokenService(JwtOptions options)
{
    private readonly SigningCredentials _signingCredentials = new(
        new SymmetricSecurityKey(Convert.FromBase64String(options.Secret)),
        SecurityAlgorithms.HmacSha256);

    private readonly TokenValidationParameters _validationParameters = new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(options.Secret)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };

    public string GenerateAccessToken(Guid userId, string username, IEnumerable<Claim>? additionalClaims = null)
    {
        var now = DateTime.UtcNow;

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.UniqueName] = username,
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
        };

        if (additionalClaims is not null)
        {
            foreach (var claim in additionalClaims)
                claims[claim.Type] = claim.Value;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(options.AccessTokenLifetime),
            SigningCredentials = _signingCredentials
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    public static string GenerateRefreshToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    /// <summary>
    /// Validates an access token and returns the claims principal.
    /// Returns null if the token is invalid or expired.
    /// </summary>
    public async Task<TokenValidationResult> ValidateAccessTokenAsync(string token)
    {
        var handler = new JsonWebTokenHandler();
        return await handler.ValidateTokenAsync(token, _validationParameters);
    }

    /// <summary>
    /// Extracts the issued-at time from a validated token result.
    /// Used to check against <c>AccessTokensInvalidatedAt</c>.
    /// </summary>
    public static DateTimeOffset? GetIssuedAt(TokenValidationResult result)
    {
        if (result.SecurityToken is JsonWebToken jwt && jwt.IssuedAt != DateTime.MinValue)
            return new DateTimeOffset(jwt.IssuedAt, TimeSpan.Zero);

        return null;
    }
}
