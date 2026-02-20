using System.Net.WebSockets;
using SharpClaw.PublicAPI.Infrastructure;

namespace SharpClaw.PublicAPI.Controllers;

/// <summary>
/// Proxies WebSocket connections from public clients to the internal
/// transcription streaming endpoint.
/// <para>
/// <c>GET /api/transcription/{jobId}/ws</c> — WebSocket proxy.<br/>
/// <c>GET /api/transcription/{jobId}/stream</c> — SSE proxy.
/// </para>
/// </summary>
public static class TranscriptionStreamingProxy
{
    public static WebApplication MapTranscriptionStreamingProxy(this WebApplication app)
    {
        app.Map("/api/jobs/{jobId:guid}/ws", HandleWebSocketProxy);
        app.Map("/api/jobs/{jobId:guid}/stream", HandleSseProxy);
        return app;
    }

    private static async Task HandleWebSocketProxy(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        var internalBaseUrl = context.RequestServices
            .GetRequiredService<IConfiguration>()
            [$"{InternalApiOptions.SectionName}:BaseUrl"] ?? "http://127.0.0.1:48923";

        var wsUri = new Uri(
            internalBaseUrl.Replace("http://", "ws://").Replace("https://", "wss://")
            + $"/jobs/{jobId}/ws");

        using var clientWs = new ClientWebSocket();

        try
        {
            await clientWs.ConnectAsync(wsUri, context.RequestAborted);
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var serverWs = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[4096];

        try
        {
            // Forward internal → public client
            while (clientWs.State == WebSocketState.Open && serverWs.State == WebSocketState.Open)
            {
                var result = await clientWs.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                await serverWs.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    context.RequestAborted);
            }

            if (serverWs.State == WebSocketState.Open)
                await serverWs.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Transcription ended.",
                    CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
    }

    private static async Task HandleSseProxy(HttpContext context)
    {
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        var api = context.RequestServices.GetRequiredService<InternalApiClient>();

        var internalBaseUrl = context.RequestServices
            .GetRequiredService<IConfiguration>()
            [$"{InternalApiOptions.SectionName}:BaseUrl"] ?? "http://127.0.0.1:48923";

        using var httpClient = new HttpClient { BaseAddress = new Uri(internalBaseUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/jobs/{jobId}/stream");

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
            while (!reader.EndOfStream && !context.RequestAborted.IsCancellationRequested)
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
