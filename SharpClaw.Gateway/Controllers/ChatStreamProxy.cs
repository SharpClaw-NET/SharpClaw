using System.Text;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Controllers;

/// <summary>
/// Proxies SSE chat streaming from the internal API to public clients.
/// <para>
/// <c>GET /api/channels/{channelId}/chat/stream</c> — channel-level SSE.<br/>
/// <c>GET /api/channels/{channelId}/chat/threads/{threadId}/stream</c> — thread-level SSE.<br/>
/// <c>POST /api/channels/{channelId}/chat/stream/approve/{jobId}</c> — channel stream approval.<br/>
/// <c>POST /api/channels/{channelId}/chat/threads/{threadId}/stream/approve/{jobId}</c> — thread stream approval.
/// </para>
/// </summary>
public static class ChatStreamProxy
{
    public static WebApplication MapChatStreamProxy(this WebApplication app)
    {
        app.Map("/api/channels/{channelId:guid}/chat/stream", HandleChannelSse);
        app.Map("/api/channels/{channelId:guid}/chat/threads/{threadId:guid}/stream", HandleThreadSse);
        app.MapPost("/api/channels/{channelId:guid}/chat/stream/approve/{jobId:guid}", HandleChannelApprove);
        app.MapPost("/api/channels/{channelId:guid}/chat/threads/{threadId:guid}/stream/approve/{jobId:guid}", HandleThreadApprove);
        return app;
    }

    private static Task HandleChannelSse(HttpContext context)
    {
        var channelId = context.Request.RouteValues["channelId"]?.ToString();
        return ProxySse(context, $"/channels/{channelId}/chat/stream");
    }

    private static Task HandleThreadSse(HttpContext context)
    {
        var channelId = context.Request.RouteValues["channelId"]?.ToString();
        var threadId = context.Request.RouteValues["threadId"]?.ToString();
        return ProxySse(context, $"/channels/{channelId}/chat/threads/{threadId}/stream");
    }

    private static Task HandleChannelApprove(HttpContext context)
    {
        var channelId = context.Request.RouteValues["channelId"]?.ToString();
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        return ProxyPost(context, $"/channels/{channelId}/chat/stream/approve/{jobId}");
    }

    private static Task HandleThreadApprove(HttpContext context)
    {
        var channelId = context.Request.RouteValues["channelId"]?.ToString();
        var threadId = context.Request.RouteValues["threadId"]?.ToString();
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        return ProxyPost(context, $"/channels/{channelId}/chat/threads/{threadId}/stream/approve/{jobId}");
    }

    private static async Task ProxyPost(HttpContext context, string internalPath)
    {
        var internalBaseUrl = context.RequestServices
            .GetRequiredService<IConfiguration>()
            [$"{InternalApiOptions.SectionName}:BaseUrl"] ?? "http://127.0.0.1:48923";

        using var httpClient = new HttpClient { BaseAddress = new Uri(internalBaseUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Post, internalPath);

        // Forward the request body
        if (context.Request.ContentLength is > 0)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, context.RequestAborted);
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = "application/json";
        var content = await response.Content.ReadAsStringAsync(context.RequestAborted);
        await context.Response.WriteAsync(content, context.RequestAborted);
    }

    private static async Task ProxySse(HttpContext context, string internalPath)
    {
        var internalBaseUrl = context.RequestServices
            .GetRequiredService<IConfiguration>()
            [$"{InternalApiOptions.SectionName}:BaseUrl"] ?? "http://127.0.0.1:48923";

        using var httpClient = new HttpClient { BaseAddress = new Uri(internalBaseUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Get, internalPath);

        // Forward query string (e.g. ?message=...)
        if (context.Request.QueryString.HasValue)
        {
            request.RequestUri = new Uri(
                httpClient.BaseAddress!, internalPath + context.Request.QueryString.Value);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        using var reader = new StreamReader(stream);

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(context.RequestAborted);
                if (line is null) break;
                await context.Response.WriteAsync(line + "\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
    }
}
