using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Security;

public sealed class EndpointGateMiddleware(
    RequestDelegate next,
    IOptionsMonitor<GatewayEndpointOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.CurrentValue.Enabled)
        {
            await GatewayErrors.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Gateway is disabled.",
                GatewayErrors.GatewayDisabled);
            return;
        }

        await next(context);
    }
}
