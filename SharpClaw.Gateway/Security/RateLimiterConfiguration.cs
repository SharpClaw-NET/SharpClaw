using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Security;

public static class RateLimiterConfiguration
{
    public const string GlobalPolicy = "global";
    public const string AuthPolicy = "auth";
    public const string ChatPolicy = "chat";

    public static IServiceCollection AddSharpClawRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // ── Rejection handler ─────────────────────────────────
            options.OnRejected = async (context, ct) =>
            {
                var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var banService = context.HttpContext.RequestServices.GetRequiredService<IpBanService>();
                banService.RecordViolation(ip);

                var path = context.HttpContext.Request.Path.Value ?? string.Empty;
                var limit = ResolveRateLimit(path);

                context.HttpContext.Response.Headers["X-RateLimit-Limit"] = limit.ToString();
                context.HttpContext.Response.Headers["X-RateLimit-Remaining"] = "0";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                    context.HttpContext.Response.Headers["X-RateLimit-Reset"] =
                        DateTimeOffset.UtcNow.Add(retryAfter).ToUnixTimeSeconds().ToString();
                }

                await GatewayErrors.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests,
                    "Too many requests. Slow down.", GatewayErrors.TooManyRequests);
            };

            // ── Global policy: sliding window ─────────────────────
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

            // ── Auth policy: stricter fixed window ────────────────
            options.AddPolicy(AuthPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // ── Chat policy: moderate sliding window ──────────────
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

    /// <summary>
    /// Infers the per-minute rate limit for a given request path so the
    /// <c>X-RateLimit-Limit</c> header reflects the applicable policy.
    /// </summary>
    public static int ResolveRateLimit(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.StartsWith("/api/auth")) return 5;
        if (lower.Contains("/chat")) return 20;
        return 60;
    }
}
