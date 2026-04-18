using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Chat;

namespace SharpClaw.Application.API.Handlers;

/// <summary>
/// SSE streaming endpoint for threaded chat. Mirrors
/// <see cref="ChatStreamHandlers"/> but scopes messages to a thread
/// so conversation history is included.
/// </summary>
[RouteGroup("/channels/{id:guid}/chat/threads/{threadId:guid}")]
public static class ThreadChatStreamHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> PendingApprovals = new();

    private const string LogCategory = "SharpClaw.SSE";

    [Conditional("DEBUG")]
    private static void Log(string message) => Debug.WriteLine(message, LogCategory);

    [MapPost("stream")]
    public static async Task StreamChat(
        HttpContext context,
        Guid id,
        Guid threadId,
        ChatRequest request,
        ChatService chatService)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        async Task<bool> ApprovalCallback(
            Contracts.DTOs.AgentActions.AgentJobResponse job, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingApprovals[job.Id] = tcs;

            using var reg = ct.Register(() => tcs.TrySetResult(false));
            try
            {
                return await tcs.Task;
            }
            finally
            {
                PendingApprovals.TryRemove(job.Id, out _);
            }
        }

        var streamId = Guid.NewGuid().ToString("N")[..8];
        var eventIndex = 0;
        var sw = Stopwatch.StartNew();
        Log($"[{streamId}] ── STREAM START ── channel={id} threadId={threadId}");

        try
        {
            // Flush response headers immediately so the client receives the
            // 200 OK and can start the SSE reader before the first event.
            await context.Response.Body.FlushAsync(context.RequestAborted);

            await foreach (var evt in chatService.SendMessageStreamAsync(
                id, request, ApprovalCallback, threadId, context.RequestAborted))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                var eventName = evt.Type.ToString();
                Log($"[{streamId}] #{eventIndex++} {eventName} ({sw.ElapsedMilliseconds}ms): {json}");
                await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n",
                    context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }

            sw.Stop();
            Log($"[{streamId}] ── STREAM END ── {eventIndex} events in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Log($"[{streamId}] ── STREAM CANCELLED ── {eventIndex} events in {sw.ElapsedMilliseconds}ms (client disconnected)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"[{streamId}] ── STREAM ERROR ── {eventIndex} events in {sw.ElapsedMilliseconds}ms: {ex.Message}");
            try
            {
                var errorEvt = ChatStreamEvent.Err(ex.Message);
                var json = JsonSerializer.Serialize(errorEvt, JsonOptions);
                await context.Response.WriteAsync($"event: Error\ndata: {json}\n\n",
                    context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
            catch { /* client may have disconnected */ }
        }
    }

    [MapPost("stream/approve/{jobId:guid}")]
    public static IResult ResolveApproval(
        HttpContext context,
        Guid id,
        Guid threadId,
        Guid jobId,
        ChatStreamApprovalRequest approval)
    {
        if (PendingApprovals.TryRemove(jobId, out var tcs))
        {
            tcs.TrySetResult(approval.Approved);
            return Results.Ok();
        }

        return Results.NotFound("No pending approval for this job.");
    }
}
