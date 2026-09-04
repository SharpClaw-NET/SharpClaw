using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway;

internal static class GatewayProxyEndpoints
{
    private static readonly HashSet<string> ExcludedRequestHeaders = new(
        ["Authorization", "Connection", "Content-Length", "Host", "Transfer-Encoding", "Upgrade", "X-Api-Key", "X-Gateway-Token"],
        StringComparer.OrdinalIgnoreCase);

    public static void MapGatewayProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/api/gateway/status", (IConfiguration configuration) => Results.Ok(new
        {
            status = "ready",
            runtime = configuration[$"{InternalApiOptions.SectionName}:BaseUrl"]
                ?? "http://127.0.0.1:48923",
        }));
        app.Map("/api/{**path}", ForwardAsync);
    }

    private static async Task ForwardAsync(
        HttpContext context,
        InternalApiClient client,
        CancellationToken cancellationToken)
    {
        var routePath = context.Request.RouteValues["path"] as string;
        var path = string.IsNullOrEmpty(routePath) ? "/" : "/" + routePath;
        var pathAndQuery = path + context.Request.QueryString;

        if (context.WebSockets.IsWebSocketRequest)
        {
            await client.ForwardWebSocketAsync(context, pathAndQuery, cancellationToken);
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

        if (context.Request.ContentLength is > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                request.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(context.Request.ContentType);
        }

        using var response = await client.SendRawAsync(request, cancellationToken);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();
        context.Response.Headers.Remove("transfer-encoding");
        if (response.Content.Headers.ContentType is { } contentType)
            context.Response.ContentType = contentType.ToString();
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }
}
