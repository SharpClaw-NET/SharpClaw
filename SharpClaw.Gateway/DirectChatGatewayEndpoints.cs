using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway;

internal static class DirectChatGatewayEndpoints
{
    public static void MapDirectChatGatewayEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/api/gateway/status", (IConfiguration configuration) => Results.Ok(new
        {
            status = "ready",
            runtime = configuration[$"{InternalApiOptions.SectionName}:BaseUrl"]
                ?? "http://127.0.0.1:48923",
        }));
        app.Map("/api/chat", ForwardAsync);
    }

    private static async Task ForwardAsync(
        HttpContext context,
        InternalApiClient client,
        CancellationToken cancellationToken)
    {
        var pathValue = context.Request.Path.Value ?? "/api/chat";
        var suffix = pathValue.Length > "/api/chat".Length
            ? pathValue["/api/chat".Length..]
            : string.Empty;
        var path = "/chat" + suffix;

        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            path + context.Request.QueryString);
        if (context.Request.ContentLength is > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                request.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(context.Request.ContentType);
        }

        using var response = await client.SendRawAsync(request, cancellationToken);
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } contentType)
            context.Response.ContentType = contentType.ToString();
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }
}
