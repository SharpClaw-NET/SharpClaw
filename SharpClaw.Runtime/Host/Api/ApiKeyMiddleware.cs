using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Exceptions;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;

namespace SharpClaw.Runtime.Host.Api;

public sealed class ApiKeyMiddleware(
    RequestDelegate next,
    ApiKeyProvider keyProvider,
    IConfiguration configuration,
    RuntimeKernelAdapter runtimeKernel)
{
    private const string HeaderName = "X-Api-Key";
    private readonly bool _disabled = configuration.GetValue<bool>("Auth:DisableApiKeyCheck");

    public async Task InvokeAsync(HttpContext context)
    {
        await runtimeKernel.RunSecurityActionAsync(
            KernelHostEndpoints.CreateExecutionContext(context),
            new SharpClawActionKey("security.api_key.resolve"),
            new RuntimeSecurityActionInvocation("resolve", context.Request.Path.Value ?? "/"),
            async (_, cancellationToken) =>
            {
                // /echo is an unauthenticated liveness check.
                if (_disabled
                    || context.Request.Path.Equals("/echo", StringComparison.OrdinalIgnoreCase)
                    || EndpointMetadataHelper.IsAnonymousAllowed(context))
                {
                    await next(context);
                    return true;
                }

                if (!context.Request.Headers.TryGetValue(HeaderName, out var providedKey) ||
                    !CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(providedKey.ToString()),
                        System.Text.Encoding.UTF8.GetBytes(keyProvider.ApiKey)))
                {
                    // 423 Locked: the API is locked to trusted local processes that hold the session key.
                    // Distinct from 401 (user identity) and 419 (expired token).
                    context.Response.StatusCode = StatusCodes.Status423Locked;
                    context.Response.Headers["WWW-Authenticate"] = "ApiKey";
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        $$"""{"error":"{{AuthErrorCodes.InvalidApiKey}}","message":"The X-Api-Key header is missing or invalid. Obtain the current session key from the local key file."}""",
                        cancellationToken);
                    return false;
                }

                await next(context);
                return true;
            },
            context.RequestAborted);
    }
}
