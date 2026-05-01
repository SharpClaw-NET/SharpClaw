using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Modules;

namespace SharpClaw.Gateway.Security;

/// <summary>
/// Middleware that checks whether the requested endpoint group is enabled
/// in <see cref="GatewayEndpointOptions"/>. Returns <c>503 Service
/// Unavailable</c> when the master switch or the specific group is off.
/// Module-contributed paths under <c>/api/modules/</c> are resolved through
/// the <see cref="GatewayEndpointGroupCatalog"/> so unknown module paths
/// short-circuit to <c>404</c> rather than leaking 5xx.
/// </summary>
public sealed class EndpointGateMiddleware(
    RequestDelegate next,
    IOptionsMonitor<GatewayEndpointOptions> options,
    GatewayEndpointGroupCatalog catalog,
    ILogger<EndpointGateMiddleware> logger)
{
    private const string ModulePathPrefix = "/api/modules/";

    public async Task InvokeAsync(HttpContext context)
    {
        var opts = options.CurrentValue;

        if (!opts.Enabled)
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable,
                "Gateway is disabled.", GatewayErrors.GatewayDisabled);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;

        // Module-contributed paths own /api/modules/* exclusively.
        if (path.StartsWith(ModulePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
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
                    match.ModuleId, match.Group.GroupId, path);
                await GatewayErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable,
                    $"The '{match.ModuleId}/{match.Group.GroupId}' endpoint is disabled.",
                    GatewayErrors.EndpointDisabled);
                return;
            }

            await next(context);
            return;
        }

        var group = ResolveGroup(path);

        if (group is not null && !opts.IsEndpointEnabled(group))
        {
            logger.LogInformation("Gateway endpoint group '{Group}' is disabled. Rejecting {Path}.", group, path);
            await GatewayErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable,
                $"The '{group}' endpoint is disabled.", GatewayErrors.EndpointDisabled);
            return;
        }

        await next(context);
    }

    private static string? ResolveGroup(string path)
    {
        // Normalise for matching
        var lower = path.ToLowerInvariant();

        if (lower.StartsWith("/api/auth"))
            return nameof(GatewayEndpointOptions.Auth);

        // Chat stream / SSE must be checked before general chat
        if (lower.Contains("/chat/stream") || lower.Contains("/chat/sse"))
            return nameof(GatewayEndpointOptions.ChatStream);

        // Cost endpoints (chat cost, thread cost, provider cost) — before general chat/providers
        if (lower.Contains("/chat/cost") || lower.Contains("/cost"))
            return nameof(GatewayEndpointOptions.Cost);

        // Thread chat
        if (lower.Contains("/threads/") && lower.Contains("/chat"))
            return nameof(GatewayEndpointOptions.ThreadChat);

        // Chat (non-thread, non-stream)
        if (lower.Contains("/chat"))
            return nameof(GatewayEndpointOptions.Chat);

        // Threads
        if (lower.Contains("/threads"))
            return nameof(GatewayEndpointOptions.Threads);

        // Jobs
        if (lower.Contains("/jobs"))
            return nameof(GatewayEndpointOptions.Jobs);

        if (lower.StartsWith("/api/agents"))
            return nameof(GatewayEndpointOptions.Agents);

        if (lower.StartsWith("/api/channels"))
            return nameof(GatewayEndpointOptions.Channels);

        if (lower.StartsWith("/api/channelcontexts") || lower.StartsWith("/api/channel-contexts"))
            return nameof(GatewayEndpointOptions.ChannelContexts);

        if (lower.StartsWith("/api/models"))
            return nameof(GatewayEndpointOptions.Models);

        if (lower.StartsWith("/api/providers"))
            return nameof(GatewayEndpointOptions.Providers);

        if (lower.StartsWith("/api/roles"))
            return nameof(GatewayEndpointOptions.Roles);

        if (lower.StartsWith("/api/users"))
            return nameof(GatewayEndpointOptions.Users);

        return null;
    }
}
