using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Providers;

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
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Validation error on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
            }
        }
        catch (NotSupportedException ex)
        {
            // Unsupported provider feature (e.g. response_mime_type on Google) → 400.
            logger.LogWarning(ex, "Unsupported operation on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
            }
        }
        catch (HttpRequestException ex)
        {
            // Provider / upstream HTTP errors → 502 Bad Gateway.
            logger.LogWarning(ex, "Provider error on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = GenericServerError }, JsonOptions));
            }
        }
    }
}
