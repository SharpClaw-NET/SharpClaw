using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host.Api;

namespace SharpClaw.Runtime.Host;

internal static class KernelHostEndpoints
{
    public static void Map(WebApplication app)
    {
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.MapGet("/echo", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/readyz", (RuntimeReadinessState readiness) =>
            readiness.IsReady
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/ping", () => Results.Ok(new { status = "authenticated" }));
        app.MapGet("/env/core", ReadEnvironmentAsync);
        app.MapPost("/chat", RunChatAsync);
        app.MapPost("/chat/stream", StreamChatAsync);
    }

    private static async Task<IResult> RunChatAsync(
        HttpContext context,
        DirectChatRequest request,
        RuntimeKernelAdapter runtimeKernel,
        DirectChatKernel kernel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return Results.BadRequest(new { error = "Message is required." });

        var result = await runtimeKernel.RunRequestAsync(
            CreateExecutionContext(context),
            request,
            (effectiveRequest, ct) => kernel.RunAsync(
                new ChatTurnInput(effectiveRequest.Message, effectiveRequest.ConversationId),
                ct),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> ReadEnvironmentAsync(
        HttpContext context,
        IConfiguration configuration,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
    {
        var allowed = await runtimeKernel.RunSecurityDecisionAsync(
            CreateExecutionContext(context),
            new SharpClawActionKey("security.secret.read"),
            new RuntimeSecurityActionInvocation("read", "/env/core"),
            static (_, _) => ValueTask.FromResult(true),
            cancellationToken);
        if (!allowed)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return Results.Ok(configuration.AsEnumerable()
            .Where(static pair => pair.Value is not null)
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value));
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
            CreateExecutionContext(context),
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

    internal static KernelActionExecutionContext CreateExecutionContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var traceId = CreateIdentity(
            Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            "trace");
        var idempotencyKey = ResolveIdempotencyKey(context, traceId);
        var features = context.Items.TryGetValue(typeof(ExtensionFeatureSet), out var value)
            && value is ExtensionFeatureSet activeFeatures
            ? activeFeatures
            : ExtensionFeatureSet.Empty;

        return new KernelActionExecutionContext(
            CreatePrincipal(context),
            features,
            traceId,
            idempotencyKey);
    }

    private static RequestPrincipal CreatePrincipal(HttpContext context)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true)
            return RequestPrincipal.Anonymous;

        var subjectId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? user.Identity.Name;
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new InvalidOperationException(
                "The authenticated HTTP principal has no stable subject identifier.");

        var displayName = user.FindFirst(ClaimTypes.Name)?.Value
            ?? user.Identity.Name;
        var roles = user.Claims
            .Where(static claim => claim.Type == ClaimTypes.Role || claim.Type == "role")
            .Select(static claim => claim.Value)
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .ToHashSet(StringComparer.Ordinal);
        return new RequestPrincipal(subjectId, displayName, roles, true);
    }

    private static Guid ResolveIdempotencyKey(HttpContext context, Guid traceId)
    {
        var header = context.Request.Headers["Idempotency-Key"].ToString();
        return string.IsNullOrWhiteSpace(header)
            ? traceId
            : CreateIdentity(header, "idempotency");
    }

    private static Guid CreateIdentity(string? value, string purpose)
    {
        if (Guid.TryParse(value, out var identity) && identity != Guid.Empty)
            return identity;
        if (string.IsNullOrWhiteSpace(value))
            return Guid.NewGuid();

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"SharpClaw:{purpose}:{value}"));
        return new Guid(digest.AsSpan(0, 16));
    }
}

public sealed record DirectChatRequest(string Message, Guid? ConversationId = null);
