using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.BLL.Services.Auth;
using SharpClaw.Contracts.Exceptions;

namespace SharpClaw.Runtime.Host.Api;

/// <summary>
/// Reads the <c>Authorization: Bearer &lt;token&gt;</c> header, validates
/// the JWT, and populates the scoped <see cref="SessionService"/> with the
/// authenticated user's ID.  Runs <b>after</b> the API-key gate so that
/// only key-authenticated requests are processed.
/// Non-exempt endpoints return <c>401</c> when no valid JWT is present.
/// <para>
/// The gateway may authenticate with a dedicated <c>X-Gateway-Token</c>
/// header instead of a user JWT. This proves the request originates from
/// the trusted gateway process and allows service-level calls (e.g. bot
/// config) that have no user context.
/// </para>
/// <para>
/// When a structurally valid JWT has <b>expired</b>, the response includes
/// a JSON body with <c>"error": "access_token_expired"</c> and a
/// <c>WWW-Authenticate: Bearer error="invalid_token"</c> header so that
/// third-party clients can detect expiry programmatically and refresh the
/// access token via <c>POST /auth/refresh</c>.
/// </para>
/// </summary>
public sealed class JwtSessionMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    ApiKeyProvider apiKeyProvider)
{
    private static readonly HashSet<string> ExemptPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/echo",
        "/ping",
        "/auth/login",
        "/auth/register",
        "/auth/refresh",
    };

    private readonly bool _disabled = configuration.GetValue<bool>("Auth:DisableAccessTokenCheck");

    public async Task InvokeAsync(HttpContext context)
    {
        var tokenExpired = false;

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            if (token.Length > 0)
            {
                var tokenService = context.RequestServices.GetRequiredService<TokenService>();
                var result = await tokenService.ValidateAccessTokenAsync(token);

                if (result.IsValid
                    && result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var sub)
                    && Guid.TryParse(sub?.ToString(), out var userId))
                {
                    // Check server-side invalidation (password change, admin action, etc.)
                    var authService = context.RequestServices.GetRequiredService<AuthService>();
                    var issuedAt = TokenService.GetIssuedAt(result);

                    if (issuedAt is not null
                        && !await authService.IsAccessTokenValidForUserAsync(userId, issuedAt.Value))
                    {
                        // Token was server-side invalidated — treat as expired
                        tokenExpired = true;
                    }
                    else
                    {
                        var session = context.RequestServices.GetRequiredService<SessionService>();
                        session.UserId = userId;
                    }
                }
                else if (result.Exception is SecurityTokenExpiredException)
                {
                    tokenExpired = true;
                }
            }
        }

        // Enforce authentication on non-exempt paths (skipped when disabled via .env).
        if (!_disabled && !IsExemptPath(context.Request.Path) && !EndpointMetadataHelper.IsAnonymousAllowed(context))
        {
            var session = context.RequestServices.GetRequiredService<SessionService>();
            if (session.UserId is null && !IsGatewayAuthenticated(context))
            {
                if (tokenExpired)
                {
                    // 419 Authentication Timeout: the token was valid but has since expired or been
                    // server-side invalidated. Clients should refresh via POST /auth/refresh.
                    // Distinct from 401 (no/bad token) and 423 (missing API key).
                    context.Response.StatusCode = 419;
                    context.Response.Headers["WWW-Authenticate"] = """Bearer error="invalid_token", error_description="The access token has expired" """.Trim();
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        $$"""{"error":"{{AccessTokenExpiredException.ErrorCode}}","message":"The access token has expired. Use your refresh token to obtain a new one via POST /auth/refresh."}""");
                }
                else
                {
                    // 401 Unauthorized: no Bearer token was provided, or the token is malformed /
                    // has an invalid signature. The client needs to log in.
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers["WWW-Authenticate"] = """Bearer error="invalid_token", error_description="No valid access token was provided" """.Trim();
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        $$"""{"error":"{{AuthErrorCodes.InvalidAccessToken}}","message":"A valid Bearer access token is required. Log in via POST /auth/login to obtain one."}""");
                }
                return;
            }
        }

        await next(context);
    }

    /// <summary>
    /// Returns <c>true</c> when the request carries a valid
    /// <c>X-Gateway-Token</c> header, proving it originates from
    /// the trusted gateway process.
    /// </summary>
    private bool IsGatewayAuthenticated(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("X-Gateway-Token", out var provided)
            || string.IsNullOrEmpty(provided.ToString()))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided.ToString()),
            System.Text.Encoding.UTF8.GetBytes(apiKeyProvider.GatewayToken));
    }

    private static bool IsExemptPath(PathString path)
    {
        var value = path.Value ?? "";
        return ExemptPaths.Contains(value);
    }
}
