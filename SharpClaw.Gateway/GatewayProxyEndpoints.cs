using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Security;

namespace SharpClaw.Gateway;

internal static class GatewayProxyEndpoints
{
    private static readonly HashSet<string> ExcludedRequestHeaders = new(
        ["Authorization", "Connection", "Content-Length", "Host", "Transfer-Encoding", "Upgrade", "X-Api-Key", "X-Gateway-Token"],
        StringComparer.OrdinalIgnoreCase);

    public static void MapGatewayProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" })).RequireRateLimiting(RateLimiterConfiguration.GlobalPolicy);
        app.MapGet("/api/gateway/status", (IConfiguration configuration) => Results.Ok(new
        {
            status = "ready",
            runtime = configuration[$"{InternalApiOptions.SectionName}:BaseUrl"]
                ?? "http://127.0.0.1:48923",
        })).RequireRateLimiting(RateLimiterConfiguration.GlobalPolicy);
        app.Map("/api/chat", ForwardAsync).RequireRateLimiting(RateLimiterConfiguration.ChatPolicy);
        app.Map("/api/chat/{**path}", ForwardAsync).RequireRateLimiting(RateLimiterConfiguration.ChatPolicy);
        app.Map("/api/{**path}", ForwardAsync).RequireRateLimiting(RateLimiterConfiguration.GlobalPolicy);
    }

    private static async Task ForwardAsync(
        HttpContext context,
        InternalApiClient client,
        string? path,
        CancellationToken cancellationToken)
    {
        if (!GatewayForwardingRoutePolicy.IsAllowed(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Catch-all binding is required for both route shapes; preserve the full chat prefix.
        _ = path;
        context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase, out var target);
        var targetPath = target.HasValue ? target.Value : "/";
        var pathAndQuery = targetPath + context.Request.QueryString;

        if (context.WebSockets.IsWebSocketRequest)
        {
            await client.ForwardWebSocketAsync(context, pathAndQuery, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            pathAndQuery);
        foreach (var header in context.Request.Headers)
        {
            if (!ExcludedRequestHeaders.Contains(header.Key))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
            context.Request.ContentLength is > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                request.Content.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }

        using var response = await client.SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();
        context.Response.Headers.Remove("transfer-encoding");
        if (response.Content.Headers.ContentType is { } contentType)
            context.Response.ContentType = contentType.ToString();
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
    }
}
