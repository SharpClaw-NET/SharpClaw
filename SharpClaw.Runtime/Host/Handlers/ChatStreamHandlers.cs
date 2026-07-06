using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Shared.Security;

namespace SharpClaw.Runtime.Host.Handlers;

/// <summary>
/// SSE streaming endpoint for chat. Streams <see cref="ChatStreamEvent"/>
/// items as server-sent events. Approval requests pause the stream;
/// the client must POST to the companion approve endpoint to continue.
/// </summary>
[RouteGroup("/channels/{id:guid}/chat")]
public static class ChatStreamHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Shared pending-approval store keyed by job ID. The SSE stream
    /// registers a TCS here; the companion POST resolves it.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> PendingApprovals = new();

    /// <summary>
    /// <c>POST /channels/{id}/chat/stream</c> — streams the response as SSE.
    /// <para>
    /// When a job needs approval, emits an <c>approval_required</c> event
    /// and waits for the client to POST to
    /// <c>/channels/{id}/chat/stream/approve/{jobId}</c> with a JSON body
    /// <c>{ "approved": true|false }</c>.
    /// </para>
    /// </summary>
    [MapPost("stream")]
    public static async Task StreamChat(
        HttpContext context,
        Guid id,
        ChatRequest request,
        ChatService chatService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("SharpClaw.SSE");
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
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "SSE chat stream {StreamId} started. ChannelId={ChannelId} ThreadId={ThreadId} ClientType={ClientType} MessageChars={MessageChars}",
                streamId, id, null, PathGuard.SanitizeForLog(request.ClientType),
                request.Message.Length);
        }

        try
        {
            // Flush response headers immediately so the client receives the
            // 200 OK and can start the SSE reader before the first event.
            // Without this, Kestrel defers header transmission until the
            // first WriteAsync, which may not happen until the provider
            // yields its first token — causing the client to buffer the
            // entire stream.
            await context.Response.Body.FlushAsync(context.RequestAborted);

            await foreach (var evt in chatService.SendMessageStreamAsync(
                id, request, ApprovalCallback, threadId: null, context.RequestAborted))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                var eventName = evt.Type.ToString();
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace(
                        "SSE chat stream {StreamId} writing event {EventIndex} {EventType} after {ElapsedMs}ms.",
                        streamId, eventIndex, eventName, sw.ElapsedMilliseconds);
                }
                eventIndex++;
                await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n",
                    context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }

            sw.Stop();
            logger.LogDebug(
                "SSE chat stream {StreamId} completed with {EventCount} events in {ElapsedMs}ms.",
                streamId, eventIndex, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            logger.LogDebug(
                "SSE chat stream {StreamId} cancelled after {EventCount} events in {ElapsedMs}ms.",
                streamId, eventIndex, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(
                ex,
                "SSE chat stream {StreamId} failed after {EventCount} events in {ElapsedMs}ms.",
                streamId, eventIndex, sw.ElapsedMilliseconds);

            // Persist the error as a system message so the user sees it
            // when they reload the thread history in a future session.
            await chatService.PersistChatErrorAsync(id, null, request, ex.Message, context.RequestAborted);

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

    /// <summary>
    /// <c>POST /channels/{id}/chat/stream/approve/{jobId}</c> — resolves
    /// a pending inline approval during a streaming chat session.
    /// </summary>
    [MapPost("stream/approve/{jobId:guid}")]
    public static IResult ResolveApproval(
        HttpContext context,
        Guid id,
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

public sealed record ChatStreamApprovalRequest(bool Approved);
