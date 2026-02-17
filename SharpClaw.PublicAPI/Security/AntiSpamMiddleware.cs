namespace SharpClaw.PublicAPI.Security;

/// <summary>
/// Enforces request body size limits and records violations against the
/// <see cref="IpBanService"/> so repeat offenders are auto-banned.
/// </summary>
public sealed class AntiSpamMiddleware(
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
            logger.LogWarning("Anti-spam: oversized body from {Ip} ({Bytes} bytes)",
                ip, context.Request.ContentLength);
            banService.RecordViolation(ip);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsync("Request body too large.");
            return;
        }

        // ── Missing Content-Type on POST/PUT ─────────────────────
        if (context.Request.Method is "POST" or "PUT"
            && context.Request.ContentType is null)
        {
            banService.RecordViolation(ip);
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await context.Response.WriteAsync("Content-Type header is required.");
            return;
        }

        await next(context);
    }
}
