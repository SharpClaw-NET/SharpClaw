using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Modules;

namespace SharpClaw.Gateway.Security;

public sealed class EndpointGateMiddleware(
    RequestDelegate next,
    IOptionsMonitor<GatewayEndpointOptions> options,
    GatewayEndpointGroupCatalog catalog,
    ILogger<EndpointGateMiddleware> logger)
{
    private const string ModulePathPrefix = "/api/modules/";

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

        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith(ModulePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var match = catalog.Resolve(path);
        if (match is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!catalog.IsEnabled(match.ModuleId, match.Group.GroupId))
        {
            logger.LogInformation(
                "Gateway module group '{ModuleId}/{GroupId}' is disabled. Rejecting {Path}.",
                match.ModuleId,
                match.Group.GroupId,
                path);
            await GatewayErrors.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                $"The '{match.ModuleId}/{match.Group.GroupId}' endpoint is disabled.",
                GatewayErrors.EndpointDisabled);
            return;
        }

        await next(context);
    }
}
