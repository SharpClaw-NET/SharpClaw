using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.Tasks;

namespace SharpClaw.Runtime.Host.Handlers;

/// <summary>
/// SSE streaming endpoint for task instance output.
/// The task itself has complete control over outputs: when to emit,
/// how often, and in what format.  This endpoint relays
/// <see cref="TaskOutputEvent"/> objects as SSE frames.
/// </summary>
[RouteGroup("/tasks/{taskId:guid}/instances/{instanceId:guid}")]
public static class TaskStreamHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// GET /tasks/{taskId}/instances/{instanceId}/stream
    /// Opens an SSE connection and relays every <see cref="TaskOutputEvent"/>
    /// the orchestrator pushes until the task completes, fails, or the
    /// client disconnects.
    /// </summary>
    [MapGet("stream")]
    public static async Task Stream(
        HttpContext context,
        Guid taskId,
        Guid instanceId,
        TaskOrchestrator orchestrator)
    {
        var reader = orchestrator.GetOutputReader(instanceId);
        if (reader is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("No active output stream for this instance.");
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        try
        {
            await foreach (var evt in reader.ReadAllAsync(context.RequestAborted))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                var eventName = evt.Type.ToString();
                await context.Response.WriteAsync(
                    $"event: {eventName}\ndata: {json}\n\n",
                    context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);

                if (evt.Type == TaskOutputEventType.Done)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — expected
        }
    }
}
