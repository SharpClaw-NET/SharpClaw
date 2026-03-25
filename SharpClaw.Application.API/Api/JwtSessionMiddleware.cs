using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.Exceptions;

namespace SharpClaw.Application.API.Api;

/// <summary>
/// Reads the <c>Authorization: Bearer &lt;token&gt;</c> header, validates
/// the JWT, and populates the scoped <see cref="SessionService"/> with the
/// authenticated user's ID.  Runs <b>after</b> the API-key gate so that
/// only key-authenticated requests are processed.
/// Non-exempt endpoints return <c>401</c> when no valid JWT is present.
/// <para>
/// When a structurally valid JWT has <b>expired</b>, the response includes
/// a JSON body with <c>"error": "access_token_expired"</c> and a
/// <c>WWW-Authenticate: Bearer error="invalid_token"</c> header so that
/// third-party clients can detect expiry programmatically and refresh the
/// access token via <c>POST /auth/refresh</c>.
/// </para>
/// </summary>
public sealed class JwtSessionMiddleware(RequestDelegate next, IConfiguration configuration)
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
        if (!_disabled && !IsExemptPath(context.Request.Path))
        {
            var session = context.RequestServices.GetRequiredService<SessionService>();
            if (session.UserId is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                if (tokenExpired)
                {
                    // Machine-readable expiry signal for third-party clients
                    context.Response.Headers["WWW-Authenticate"] = """Bearer error="invalid_token", error_description="The access token has expired" """.Trim();
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        $$"""{"error":"{{AccessTokenExpiredException.ErrorCode}}","message":"The access token has expired. Use your refresh token to obtain a new one via POST /auth/refresh."}""");
                }
                else
                {
                    await context.Response.WriteAsync("Authentication required.");
                }
                return;
            }
        }

        await next(context);
    }

    private static bool IsExemptPath(PathString path)
    {
        var value = path.Value ?? "";
        // Exact matches
        if (ExemptPaths.Contains(value))
            return true;
        // Editor WebSocket endpoint has its own handshake auth
        if (value.StartsWith("/editor/ws", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
