using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;

namespace SharpClaw.Application.API.Handlers;

/// <summary>
/// SSE watch endpoint for real-time thread activity notifications.
/// Clients subscribe to receive <c>Processing</c> and <c>NewMessages</c>
/// events so they can update their UI without polling.
/// </summary>
[RouteGroup("/channels/{id:guid}/chat/threads/{threadId:guid}")]
public static class ThreadWatchHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    [MapGet("watch")]
    public static async Task Watch(
        HttpContext context,
        Guid id,
        Guid threadId,
        ThreadActivitySignal signal)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        using var subscription = signal.Subscribe(threadId);
        var ct = context.RequestAborted;

        try
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                var eventName = evt.Type.ToString();
                await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal
        }
    }
}
