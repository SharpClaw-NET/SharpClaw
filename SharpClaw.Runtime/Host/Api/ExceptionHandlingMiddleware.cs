using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.Host.Api;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    private const int ClientClosedRequestStatusCode = 499;
    private const string GenericServerError = "An internal server error occurred.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException ex) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Request cancelled on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;

            context.Response.StatusCode = ClientClosedRequestStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new { error = GenericServerError },
                JsonOptions));
        }
    }
}
