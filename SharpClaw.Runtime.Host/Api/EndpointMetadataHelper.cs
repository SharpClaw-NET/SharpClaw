using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace SharpClaw.Runtime.Host.Api;

/// <summary>
/// Inspects resolved endpoint metadata for standard ASP.NET authorization
/// attributes (<see cref="AllowAnonymousAttribute"/>, <see cref="AuthorizeAttribute"/>).
/// Used by custom auth middlewares to honour the same conventions that
/// ASP.NET Core's built-in authorization middleware respects.
/// </summary>
internal static class EndpointMetadataHelper
{
    /// <summary>
    /// Returns <c>true</c> when the matched endpoint (if any) carries
    /// <see cref="IAllowAnonymous"/> metadata, meaning the request should
    /// bypass authentication checks.
    /// </summary>
    public static bool IsAnonymousAllowed(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        return endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
    }

    /// <summary>
    /// Returns <c>true</c> when the matched endpoint explicitly carries an
    /// <see cref="IAuthorizeData"/> attribute (e.g. <c>[Authorize]</c>,
    /// <c>[Authorize(Roles = "...")]</c>). Useful for future per-role
    /// enforcement in custom middleware.
    /// </summary>
    public static bool RequiresAuthorization(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        return endpoint?.Metadata.GetMetadata<IAuthorizeData>() is not null;
    }
}
