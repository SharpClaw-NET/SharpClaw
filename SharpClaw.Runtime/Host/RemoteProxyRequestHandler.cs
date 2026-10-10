using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.Host;

internal sealed class RemoteProxyRequestHandler(
    RemoteProxyActionBoundary actions,
    RemoteGatewayProxy proxy,
    RemoteGatewayConnection connection,
    ApiKeyProvider keys)
{
    internal async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path.Value ?? "/";
        var executionContext = KernelHostEndpoints.CreateExecutionContext(context);
        var anonymousEcho = string.Equals(path, "/echo", StringComparison.OrdinalIgnoreCase);
        var authenticated = anonymousEcho || HasExactApiKey(context, keys.ApiKey);
        if (!await actions.ResolveApiKeyAsync(executionContext, path, authenticated, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            context.Response.Headers.WWWAuthenticate = "ApiKey";
            return;
        }
        if (!await actions.ValidatePairingAsync(executionContext, path,
                RemoteProxyRoutePolicy.IsAllowed(context), context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await actions.RunRequestAsync(executionContext,
            new RemoteProxyRequestInvocation(context.Request.Method, path),
            token => RunHandlerAsync(context, token), context.RequestAborted).ConfigureAwait(false);
    }

    private static bool HasExactApiKey(HttpContext context, string expectedKey) =>
        context.Request.Headers.TryGetValue("X-Api-Key", out var providedKey)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(providedKey.ToString()), Encoding.UTF8.GetBytes(expectedKey));

    private async ValueTask RunHandlerAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (IsLocalDiagnostic(context.Request.Path))
        {
            await RunDiagnosticAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            await proxy.ForwardAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        catch (WebSocketException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    private static bool IsLocalDiagnostic(PathString path) =>
        path.Equals("/echo", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/ping", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/readyz", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/remote/status", StringComparison.OrdinalIgnoreCase);

    private async ValueTask RunDiagnosticAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }
        if (context.Request.Path.Equals("/remote/status", StringComparison.OrdinalIgnoreCase))
        {
            await context.Response.WriteAsJsonAsync(new
            {
                mode = "gateway-proxy",
                gatewayUrl = connection.GatewayBaseUri.AbsoluteUri,
                connected = await proxy.IsReadyAsync(cancellationToken).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (context.Request.Path.Equals("/readyz", StringComparison.OrdinalIgnoreCase))
        {
            if (!await proxy.IsReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            await context.Response.WriteAsJsonAsync(new { status = "ready" }, cancellationToken).ConfigureAwait(false);
            return;
        }
        var status = context.Request.Path.Equals("/echo", StringComparison.OrdinalIgnoreCase) ? "ok"
            : context.Request.Path.Equals("/ping", StringComparison.OrdinalIgnoreCase) ? "authenticated" : "healthy";
        await context.Response.WriteAsJsonAsync(new { status }, cancellationToken).ConfigureAwait(false);
    }
}
