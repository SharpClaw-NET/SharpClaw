using SharpClaw.Gateway.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

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

        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true &&
            context.Request.ContentLength is not > 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Admit at most64KiB before forwarding. The original request stream is
        // borrowed; only this bounded buffer is ours to retire after next settles.
        using var admitted = new MemoryStream();
        var buffer = new byte[8192];
        var original = context.Request.Body;
        while (true)
        {
            var remaining = (int)(MaxBodySizeBytes + 1 - admitted.Length);
            var read = await original.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), context.RequestAborted).ConfigureAwait(false);
            if (read == 0) break;
            await admitted.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
            if (admitted.Length > MaxBodySizeBytes)
            {
                GatewayLog.OversizedBody(logger, ip, admitted.Length);
                banService.RecordViolation(ip);
                await GatewayErrors.WriteAsync(context, StatusCodes.Status413PayloadTooLarge,
                    "Request body too large.", GatewayErrors.PayloadTooLarge).ConfigureAwait(false);
                return;
            }
        }
        admitted.Position = 0;
        context.Request.Body = admitted;
        try { await next(context).ConfigureAwait(false); }
        finally { context.Request.Body = original; }
    }
}
