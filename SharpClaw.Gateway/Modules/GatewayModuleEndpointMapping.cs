using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Abstractions;
using SharpClaw.Gateway.Modules.Hosting;
using SharpClaw.Gateway.Modules.Routing;
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
/// <remarks>
/// Phase 5a: mapping delegates to <see cref="GatewayModuleHostManager"/>
/// which registers a single <see cref="ModuleEndpointDataSource"/> on the
/// app and reconciles it whenever options change (when
/// <see cref="GatewayModuleOptions.HotReloadEnabled"/> is true).
/// </remarks>
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

        var manager = app.Services.GetRequiredService<GatewayModuleHostManager>();
        var dataSource = app.Services.GetRequiredService<ModuleEndpointDataSource>();
        var optionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<GatewayModuleOptions>>();
        var logger = app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("SharpClaw.Gateway.Modules");

        // Wire the dynamic data source into ASP.NET Core's endpoint routing.
        // Adding twice is harmless (duplicate detection in EndpointDataSource
        // composite is by reference); the assertion below guards against it.
        var dataSources = ((IEndpointRouteBuilder)app).DataSources;
        if (!dataSources.Contains(dataSource))
            dataSources.Add(dataSource);

        // Initial reconcile against the current options snapshot.
        manager.ApplyDesiredStateAsync(optionsMonitor.CurrentValue).GetAwaiter().GetResult();

        // Subscribe to .env reloads only when hot reload is opted in. The
        // subscription is process-lifetime; the manager itself is the
        // application's IOptionsMonitor sink.
        if (optionsMonitor.CurrentValue.HotReloadEnabled)
        {
            optionsMonitor.OnChange(opts =>
            {
                if (!opts.HotReloadEnabled) return;
                _ = ReconcileSafely(manager, opts, logger);
            });
            logger.LogInformation("Gateway module hot reload is enabled (IOptionsMonitor subscribed).");
        }

        return app;
    }

    private static async Task ReconcileSafely(
        GatewayModuleHostManager manager,
        GatewayModuleOptions opts,
        ILogger logger)
    {
        try
        {
            await manager.ApplyDesiredStateAsync(opts).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gateway module reconcile from options change failed.");
        }
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
