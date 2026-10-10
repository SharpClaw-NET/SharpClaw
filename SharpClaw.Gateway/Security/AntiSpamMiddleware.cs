using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Security;

/// <summary>
/// Enforces request body size limits and records violations against the
/// <see cref="IpBanService"/> so repeat offenders are auto-banned.
/// </summary>
internal sealed class AntiSpamMiddleware(
    RequestDelegate next,
    IpBanService banService,
    ILogger<AntiSpamMiddleware> logger)
{
    /// <summary>Maximum allowed request body size in bytes (default 64 KB).</summary>
    private const long MaxBodySizeBytes = 64 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // ── Body size check ──────────────────────────────────────
        if (context.Request.ContentLength > MaxBodySizeBytes)
        {
            GatewayLog.OversizedBody(logger, ip, context.Request.ContentLength);
            banService.RecordViolation(ip);
            await GatewayErrors.WriteAsync(context, StatusCodes.Status413PayloadTooLarge,
                "Request body too large.", GatewayErrors.PayloadTooLarge).ConfigureAwait(false);
            return;
        }

        // ── Missing Content-Type on POST/PUT ─────────────────────
        if ((string.Equals(context.Request.Method, "POST", StringComparison.Ordinal)
            || string.Equals(context.Request.Method, "PUT", StringComparison.Ordinal))
            && context.Request.ContentType is null)
        {
            banService.RecordViolation(ip);
            await GatewayErrors.WriteAsync(context, StatusCodes.Status415UnsupportedMediaType,
                "Content-Type header is required.", GatewayErrors.UnsupportedMediaType).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
