using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Security;

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
            await GatewayErrors.WriteAsync(context, StatusCodes.Status403Forbidden,
                "Forbidden.", GatewayErrors.IpBanned);
            return;
        }

        await next(context);
    }
}
