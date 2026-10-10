using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.Host.Api;

internal sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    private const int ClientClosedRequestStatusCode = 499;
    private const string GenericServerError = "An internal server error occurred.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly Action<ILogger, string, PathString, Exception?> LogCancelled =
        LoggerMessage.Define<string, PathString>(LogLevel.Debug,
            new EventId(1, nameof(LogCancelled)), "Request cancelled on {Method} {Path}");
    private static readonly Action<ILogger, string, PathString, Exception?> LogUnhandled =
        LoggerMessage.Define<string, PathString>(LogLevel.Error,
            new EventId(2, nameof(LogUnhandled)), "Unhandled exception on {Method} {Path}");

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (context.RequestAborted.IsCancellationRequested)
        {
            LogCancelled(logger, context.Request.Method, context.Request.Path, ex);
            if (context.Response.HasStarted)
                throw;

            context.Response.StatusCode = ClientClosedRequestStatusCode;
        }
        catch (Exception ex)
        {
            LogUnhandled(logger, context.Request.Method, context.Request.Path, ex);
            if (context.Response.HasStarted)
                throw;

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new { error = GenericServerError },
                JsonOptions), context.RequestAborted).ConfigureAwait(false);
        }
    }
}
