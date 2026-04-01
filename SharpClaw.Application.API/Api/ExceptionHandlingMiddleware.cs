using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Serilog;
using SharpClaw.Application.Core.Clients;

namespace SharpClaw.Application.API.Api;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next)
{
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
        catch (CompletionParameterValidationException ex)
        {
            Log.Warning(ex, "Completion parameter validation failed on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = "Invalid completion parameters",
                    provider = ex.ProviderType.ToString(),
                    validationErrors = ex.ValidationErrors,
                }, JsonOptions));
            }
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning(ex, "Validation error on {Method} {Path}", context.Request.Method, context.Request.Path);
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
            Log.Warning(ex, "Unsupported operation on {Method} {Path}", context.Request.Method, context.Request.Path);
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
            Log.Warning(ex, "Provider error on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
            }
        }
    }
}
