using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Modules;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;

internal static class KernelHostEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/echo", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/readyz", (RuntimeReadinessState readiness) =>
            readiness.IsReady
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/ping", () => Results.Ok(new { status = "authenticated" }));
        app.MapGet("/env/core", (IConfiguration configuration) => Results.Ok(
            configuration.AsEnumerable()
                .Where(static pair => pair.Value is not null)
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)));
        app.MapPost("/chat", RunChatAsync);
        app.MapPost("/chat/stream", StreamChatAsync);
    }

    private static async Task<IResult> RunChatAsync(
        DirectChatRequest request,
        RuntimeKernelAdapter runtimeKernel,
        DirectChatKernel kernel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return Results.BadRequest(new { error = "Message is required." });

        var result = await runtimeKernel.RunRequestAsync(
            request,
            (effectiveRequest, ct) => kernel.RunAsync(
                new ChatTurnInput(effectiveRequest.Message, effectiveRequest.ConversationId),
                ct),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task StreamChatAsync(
        HttpContext context,
        DirectChatRequest request,
        RuntimeKernelAdapter runtimeKernel,
        DirectChatKernel kernel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { error = "Message is required." },
                cancellationToken);
            return;
        }

        context.Response.ContentType = "text/event-stream";
        var result = await runtimeKernel.RunRequestAsync(
            request,
            (effectiveRequest, ct) => kernel.RunAsync(
                new ChatTurnInput(effectiveRequest.Message, effectiveRequest.ConversationId),
                ct),
            cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            conversationId = result.ConversationId,
            turnId = result.TurnId,
            content = result.Completion.Content,
            finishReason = result.Completion.FinishReason.ToString(),
        });
        await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
    }
}

public sealed record DirectChatRequest(string Message, Guid? ConversationId = null);
