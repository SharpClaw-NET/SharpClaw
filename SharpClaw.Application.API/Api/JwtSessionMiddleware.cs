using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;

namespace SharpClaw.Application.API.Api;

/// <summary>
/// Reads the <c>Authorization: Bearer &lt;token&gt;</c> header, validates
/// the JWT, and populates the scoped <see cref="SessionService"/> with the
/// authenticated user's ID.  Runs <b>after</b> the API-key gate so that
/// only key-authenticated requests are processed.
/// </summary>
public sealed class JwtSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
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
                    var session = context.RequestServices.GetRequiredService<SessionService>();
                    session.UserId = userId;
                }
            }
        }

        await next(context);
    }
}
