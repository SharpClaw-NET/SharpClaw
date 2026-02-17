namespace SharpClaw.PublicAPI.Security;

/// <summary>
/// Rejects requests from banned IPs with <c>403 Forbidden</c>.
/// </summary>
public sealed class IpBanMiddleware(RequestDelegate next, IpBanService banService)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (banService.IsBanned(ip))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden.");
            return;
        }

        await next(context);
    }
}
