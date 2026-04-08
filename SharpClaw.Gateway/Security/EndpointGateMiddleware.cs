using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Security;

/// <summary>
/// Middleware that checks whether the requested endpoint group is enabled
/// in <see cref="GatewayEndpointOptions"/>. Returns <c>503 Service
/// Unavailable</c> when the master switch or the specific group is off.
/// </summary>
public sealed class EndpointGateMiddleware(
    RequestDelegate next,
    IOptionsMonitor<GatewayEndpointOptions> options,
    ILogger<EndpointGateMiddleware> logger)
{
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

        if (lower.StartsWith("/api/input-audios"))
            return nameof(GatewayEndpointOptions.InputAudios);

        // Transcription streaming (WS/SSE proxy)
        if (lower.Contains("/ws") || lower.Contains("/stream"))
            return nameof(GatewayEndpointOptions.TranscriptionStreaming);

        if (lower.StartsWith("/api/transcription"))
            return nameof(GatewayEndpointOptions.Transcription);

        if (lower.StartsWith("/api/bots"))
            return nameof(GatewayEndpointOptions.Bots);

        return null;
    }
}
