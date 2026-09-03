using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Security;

public static class RateLimiterConfiguration
{
    public const string GlobalPolicy = "global";
    public const string ChatPolicy = "chat";

    public static IServiceCollection AddSharpClawRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.OnRejected = async (context, _) =>
            {
                var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var banService = context.HttpContext.RequestServices.GetRequiredService<IpBanService>();
                banService.RecordViolation(ip);

                var path = context.HttpContext.Request.Path.Value ?? string.Empty;
                var catalog = context.HttpContext.RequestServices
                    .GetService<Modules.GatewayEndpointGroupCatalog>();
                var limit = ResolveRateLimit(path, catalog);

                context.HttpContext.Response.Headers["X-RateLimit-Limit"] = limit.ToString();
                context.HttpContext.Response.Headers["X-RateLimit-Remaining"] = "0";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                    context.HttpContext.Response.Headers["X-RateLimit-Reset"] =
                        DateTimeOffset.UtcNow.Add(retryAfter).ToUnixTimeSeconds().ToString();
                }

                await GatewayErrors.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    "Too many requests. Slow down.",
                    GatewayErrors.TooManyRequests);
            };

            options.AddPolicy(GlobalPolicy, context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.AddPolicy(ChatPolicy, context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));
        });

        return services;
    }

    public static int ResolveRateLimit(
        string path,
        Modules.GatewayEndpointGroupCatalog? catalog = null)
    {
        if (catalog is not null
            && path.StartsWith("/api/modules/", StringComparison.OrdinalIgnoreCase)
            && catalog.Resolve(path) is { } match)
        {
            return match.Group.RateLimitPolicy switch
            {
                ChatPolicy => 20,
                _ => 60,
            };
        }

        return path.Contains("/chat", StringComparison.OrdinalIgnoreCase) ? 20 : 60;
    }
}
