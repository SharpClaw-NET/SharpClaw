using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Abstractions;
using SharpClaw.Gateway.Security;
using SharpClaw.Utils.Security;

namespace SharpClaw.Gateway.Modules;

/// <summary>
/// Wires discovered <see cref="IGatewayModuleExtension"/> instances into the
/// running ASP.NET Core endpoint table. Each enabled module's enabled groups
/// become a rate-limited <see cref="RouteGroupBuilder"/> beneath
/// <c>/api/modules/{ModuleId}/{GroupId}</c> and are recorded in the
/// <see cref="GatewayEndpointGroupCatalog"/> so the endpoint gate and rate
/// limiter can resolve incoming requests back to their module identity.
/// </summary>
public static class GatewayModuleEndpointMapping
{
    /// <summary>
    /// Marker key set by <c>Program.cs</c> immediately after
    /// <c>app.UseRateLimiter()</c>. The mapping extension reads it to enforce
    /// the documented ordering at startup; calling
    /// <see cref="MapGatewayModuleEndpoints"/> before the rate limiter is
    /// registered would cause module routes to silently bypass per-policy
    /// limits.
    /// </summary>
    public const string RateLimiterReadyKey = "SharpClaw:Gateway:RateLimiterUsed";

    /// <summary>
    /// Maps every enabled module's enabled groups onto <paramref name="app"/>.
    /// Must be called <strong>after</strong> <c>app.UseRateLimiter()</c>;
    /// the method asserts this and throws when called too early.
    /// </summary>
    public static WebApplication MapGatewayModuleEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        AssertRateLimiterRegistered(app);

        var loader = app.Services.GetRequiredService<GatewayModuleLoader>();
        var catalog = app.Services.GetRequiredService<GatewayEndpointGroupCatalog>();
        var moduleOpts = app.Services.GetRequiredService<IOptions<GatewayModuleOptions>>().Value;
        var logger = app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("SharpClaw.Gateway.Modules");

        foreach (var ext in loader.All)
        {
            if (!moduleOpts.IsModuleEnabled(ext.ModuleId))
                continue;

            foreach (var group in ext.GetEndpointGroups())
            {
                if (!moduleOpts.IsGroupEnabled(ext.ModuleId, group.GroupId))
                    continue;

                var prefix = $"/api/modules/{ext.ModuleId}/{group.GroupId}";

                if (!catalog.TryRegister(ext.ModuleId, group))
                {
                    logger.LogError(
                        "Gateway module group {Prefix} is already registered; skipping duplicate from {ModuleId}.",
                        prefix,
                        PathGuard.SanitizeForLog(ext.ModuleId));
                    continue;
                }

                var routeGroup = app.MapGroup(prefix);
                var policy = group.RateLimitPolicy ?? RateLimiterConfiguration.GlobalPolicy;
                routeGroup.RequireRateLimiting(policy);

                var builder = new GatewayEndpointGroupBuilder(routeGroup, group.GroupId, prefix);
                try
                {
                    ext.MapEndpoints(builder);
                    logger.LogInformation(
                        "Gateway module endpoint group registered: {Prefix} ({DisplayName}) policy={Policy}",
                        prefix,
                        group.DisplayName,
                        policy);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Gateway module {ModuleId} threw while mapping group {GroupId}; unregistering prefix.",
                        PathGuard.SanitizeForLog(ext.ModuleId),
                        PathGuard.SanitizeForLog(group.GroupId));
                    catalog.Unregister(ext.ModuleId, group.GroupId);
                }
            }
        }

        return app;
    }

    private static void AssertRateLimiterRegistered(WebApplication app)
    {
        var properties = ((IApplicationBuilder)app).Properties;
        if (properties.TryGetValue(RateLimiterReadyKey, out var marker)
            && marker is true)
        {
            return;
        }

        throw new InvalidOperationException(
            "MapGatewayModuleEndpoints must be called after app.UseRateLimiter(); "
            + $"set ((IApplicationBuilder)app).Properties[\"{RateLimiterReadyKey}\"] = true immediately after "
            + "the rate limiter middleware is registered.");
    }
}
