using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SharpClaw.PublicAPI.Security;

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

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                }

                await context.HttpContext.Response.WriteAsync(
                    "Too many requests. Slow down.", ct);
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
}
