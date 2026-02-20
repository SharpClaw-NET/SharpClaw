using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Transcription;

namespace SharpClaw.Application.API.Handlers;

/// <summary>
/// Registers WebSocket and SSE endpoints for live transcription streaming.
/// Uses <see cref="AgentJobService"/> channels for segment broadcast.
/// </summary>
public static class TranscriptionWebSocketEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Maps the WebSocket and SSE streaming endpoints.
    /// Must be called after <c>app.UseWebSockets()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapTranscriptionStreaming(this IEndpointRouteBuilder routes)
    {
        routes.Map("/jobs/{jobId:guid}/ws", HandleWebSocket);
        routes.Map("/jobs/{jobId:guid}/stream", HandleSse);
        return routes;
    }

    private static async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var jobId = Guid.Parse((string)context.Request.RouteValues["jobId"]!);

        using var scope = context.RequestServices.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AgentJobService>();

        var reader = svc.Subscribe(jobId);
        if (reader is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();

        try
        {
            await foreach (var segment in reader.ReadAllAsync(context.RequestAborted))
            {
                var json = JsonSerializer.Serialize(segment, JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json);

                await ws.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    context.RequestAborted);
            }

            await ws.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Transcription ended.",
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnected.",
                    CancellationToken.None);
        }
    }

    private static async Task HandleSse(HttpContext context)
    {
        var jobId = Guid.Parse((string)context.Request.RouteValues["jobId"]!);

        using var scope = context.RequestServices.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AgentJobService>();

        var reader = svc.Subscribe(jobId);
        if (reader is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        try
        {
            await foreach (var segment in reader.ReadAllAsync(context.RequestAborted))
            {
                var json = JsonSerializer.Serialize(segment, JsonOptions);
                await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }

            // Signal end
            await context.Response.WriteAsync("event: done\ndata: {}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
    }
}
