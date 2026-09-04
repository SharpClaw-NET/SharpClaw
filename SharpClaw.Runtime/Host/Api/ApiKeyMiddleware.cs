using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Exceptions;
using SharpClaw.Contracts.Kernel;
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
        var allowed = await runtimeKernel.RunSecurityDecisionAsync(
            KernelHostEndpoints.CreateExecutionContext(context),
            new SharpClawActionKey("security.api_key.resolve"),
            new RuntimeSecurityActionInvocation("resolve", context.Request.Path.Value ?? "/"),
            (_, _) =>
            {
                // /echo is an unauthenticated liveness check.
                var baseAllowed = _disabled
                    || context.Request.Path.Equals("/echo", StringComparison.OrdinalIgnoreCase)
                    || EndpointMetadataHelper.IsAnonymousAllowed(context)
                    || HasValidApiKey(context, keyProvider.ApiKey);
                return ValueTask.FromResult(baseAllowed);
            },
            context.RequestAborted);

        if (!allowed)
        {
            // 423 Locked: the API is locked to trusted local processes that hold the session key.
            // Distinct from 401 (user identity) and 419 (expired token).
            context.Response.StatusCode = StatusCodes.Status423Locked;
            context.Response.Headers["WWW-Authenticate"] = "ApiKey";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                $$"""{"error":"{{AuthErrorCodes.InvalidApiKey}}","message":"The X-Api-Key header is missing or invalid. Obtain the current session key from the local key file."}""",
                context.RequestAborted);
            return;
        }

        // The protected pipeline is outside the repeatable security decision.
        await next(context);
    }

    private static bool HasValidApiKey(HttpContext context, string expectedKey)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var providedKey))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(providedKey.ToString()),
            System.Text.Encoding.UTF8.GetBytes(expectedKey));
    }
}
