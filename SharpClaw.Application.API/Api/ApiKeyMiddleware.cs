using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace SharpClaw.Application.API.Api;

public sealed class ApiKeyMiddleware(RequestDelegate next, ApiKeyProvider keyProvider, IConfiguration configuration)
{
    private const string HeaderName = "X-Api-Key";
    private readonly bool _disabled = configuration.GetValue<bool>("Auth:DisableApiKeyCheck");

    public async Task InvokeAsync(HttpContext context)
    {
        // /echo is an unauthenticated liveness check.
        if (_disabled || context.Request.Path.Equals("/echo", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var providedKey) ||
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(providedKey.ToString()),
                System.Text.Encoding.UTF8.GetBytes(keyProvider.ApiKey)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid or missing API key.");
            return;
        }

        await next(context);
    }
}
