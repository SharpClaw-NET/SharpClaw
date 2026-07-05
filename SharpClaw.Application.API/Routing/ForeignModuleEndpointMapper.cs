using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Net.WebSockets;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Modules.Foreign;
using SharpClaw.Contracts.Modules.Foreign;

namespace SharpClaw.Application.API.Routing;

public static class ForeignModuleEndpointMapper
{
    private static readonly HashSet<string> SupportedMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            HttpMethods.Get,
            HttpMethods.Post,
            HttpMethods.Put,
            HttpMethods.Delete,
            HttpMethods.Patch,
            HttpMethods.Head,
            HttpMethods.Options,
        };

    private static readonly HashSet<string> SupportedResponseModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ForeignModuleEndpointResponseMode.Json,
            ForeignModuleEndpointResponseMode.Raw,
            ForeignModuleEndpointResponseMode.Static,
            ForeignModuleEndpointResponseMode.Stream,
            ForeignModuleEndpointResponseMode.WebSocket,
        };

    public static IEndpointRouteBuilder MapForeignModuleEndpoints(
        this IEndpointRouteBuilder routes,
        ModuleRegistry registry)
    {
        var logger = routes.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ForeignModuleEndpointMapper).FullName!);
        var occupiedRoutes = BuildOccupiedRouteSet(routes.DataSources);
        var mapped = 0;
        var skipped = 0;

        foreach (var host in registry.GetRuntimeHosts().OfType<IForeignModuleRuntimeHost>())
        {
            var moduleId = host.Module.Id;
            foreach (var endpoint in host.Endpoints)
            {
                if (!TryValidate(endpoint, out var method, out var error))
                {
                    skipped++;
                    logger.LogError(
                        "Skipping foreign endpoint for module '{ModuleId}': {Error}",
                        moduleId,
                        error);
                    continue;
                }

                var key = RouteKey(method, endpoint.RoutePattern);
                if (!occupiedRoutes.Add(key))
                {
                    skipped++;
                    logger.LogError(
                        "Skipping foreign endpoint {Method} {RoutePattern} for module '{ModuleId}' because that route is already mapped.",
                        method,
                        endpoint.RoutePattern,
                        moduleId);
                    continue;
                }

                var builder = routes.MapMethods(
                    endpoint.RoutePattern,
                    [method],
                    context => ProxyAsync(context, registry, moduleId, endpoint));
                if (string.Equals(
                        endpoint.AuthPolicy,
                        ForeignModuleEndpointAuthPolicy.Anonymous,
                        StringComparison.OrdinalIgnoreCase))
                {
                    builder.AllowAnonymous();
                }

                mapped++;
            }
        }

        if (skipped > 0)
        {
            logger.LogWarning(
                "Mapped {Mapped} foreign module endpoint(s); skipped {Skipped}.",
                mapped,
                skipped);
        }
        else
        {
            logger.LogDebug("Mapped {Mapped} foreign module endpoint(s).", mapped);
        }

        return routes;
    }

    private static async Task ProxyAsync(
        HttpContext context,
        ModuleRegistry registry,
        string moduleId,
        ForeignModuleEndpointDescriptor descriptor)
    {
        if (registry.GetRuntimeHost(moduleId) is not IForeignModuleRuntimeHost host)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Module '{moduleId}' is not available.",
            }, context.RequestAborted);
            return;
        }

        if (!host.TryAcquireExecution())
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Module '{moduleId}' is unloading.",
            }, context.RequestAborted);
            return;
        }

        try
        {
            if (string.Equals(
                    descriptor.ResponseMode,
                    ForeignModuleEndpointResponseMode.WebSocket,
                    StringComparison.OrdinalIgnoreCase))
            {
                await ProxyWebSocketAsync(context, host);
                return;
            }

            using var outgoing = CreateProxyRequest(context.Request);
            using var response = await host.SendEndpointRequestAsync(outgoing, context.RequestAborted);
            await CopyProxyResponseAsync(response, context);
        }
        finally
        {
            host.ReleaseExecution();
        }
    }

    private static async Task ProxyWebSocketAsync(
        HttpContext context,
        IForeignModuleRuntimeHost host)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connections only.", context.RequestAborted);
            return;
        }

        var forwardedHeaders = context.Request.Headers.ToDictionary(
            header => header.Key,
            header => (IReadOnlyList<string>)header.Value
                .Where(value => value is not null)
                .Select(value => value!)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var pathAndQuery = context.Request.Path + context.Request.QueryString;

        using var sidecarSocket = await host.ConnectEndpointWebSocketAsync(
            pathAndQuery,
            forwardedHeaders,
            context.RequestAborted);
        using var clientSocket = await context.WebSockets.AcceptWebSocketAsync();

        var clientToSidecar = PumpWebSocketAsync(clientSocket, sidecarSocket, context.RequestAborted);
        var sidecarToClient = PumpWebSocketAsync(sidecarSocket, clientSocket, context.RequestAborted);
        await Task.WhenAll(clientToSidecar, sidecarToClient);
    }

    private static async Task PumpWebSocketAsync(
        WebSocket source,
        WebSocket destination,
        CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];

        while (!ct.IsCancellationRequested
               && source.State is WebSocketState.Open or WebSocketState.CloseReceived
               && destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var result = await source.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await destination.CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        CancellationToken.None);
                }
                return;
            }

            await destination.SendAsync(
                new ArraySegment<byte>(buffer, 0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                ct);
        }
    }

    private static HttpRequestMessage CreateProxyRequest(HttpRequest request)
    {
        var uri = request.Path + request.QueryString;
        var outgoing = new HttpRequestMessage(
            new HttpMethod(request.Method),
            uri);

        if (RequestMayHaveBody(request))
            outgoing.Content = new StreamContent(request.Body);

        foreach (var (name, values) in request.Headers)
        {
            if (string.Equals(name, HeaderNames.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(name, ForeignModuleProtocol.TokenHeaderName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!outgoing.Headers.TryAddWithoutValidation(name, values.ToArray())
                && outgoing.Content is not null)
            {
                outgoing.Content.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        return outgoing;
    }

    private static bool RequestMayHaveBody(HttpRequest request) =>
        request.ContentLength is > 0 ||
        request.Headers.ContainsKey(HeaderNames.TransferEncoding) ||
        HttpMethods.IsPost(request.Method) ||
        HttpMethods.IsPut(request.Method) ||
        HttpMethods.IsPatch(request.Method);

    private static async Task CopyProxyResponseAsync(
        HttpResponseMessage source,
        HttpContext context)
    {
        context.Response.StatusCode = (int)source.StatusCode;

        foreach (var header in source.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();

        foreach (var header in source.Content.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();

        context.Response.Headers.Remove(HeaderNames.TransferEncoding);
        await source.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static bool TryValidate(
        ForeignModuleEndpointDescriptor endpoint,
        out string method,
        out string error)
    {
        method = endpoint.Method.Trim().ToUpperInvariant();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(method) || !SupportedMethods.Contains(method))
        {
            error = $"HTTP method '{endpoint.Method}' is not supported.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(endpoint.RoutePattern)
            || !endpoint.RoutePattern.StartsWith("/", StringComparison.Ordinal))
        {
            error = $"Route pattern '{endpoint.RoutePattern}' must be an absolute path.";
            return false;
        }

        if (endpoint.RoutePattern.StartsWith("/.sharpclaw/", StringComparison.OrdinalIgnoreCase))
        {
            error = "Control-plane routes cannot be exposed as module endpoints.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(endpoint.ResponseMode)
            || !SupportedResponseModes.Contains(endpoint.ResponseMode))
        {
            error = $"Response mode '{endpoint.ResponseMode}' is not supported.";
            return false;
        }

        if (string.Equals(endpoint.ResponseMode, ForeignModuleEndpointResponseMode.WebSocket, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase))
        {
            error = "WebSocket endpoints must use GET.";
            return false;
        }

        try
        {
            RoutePatternFactory.Parse(endpoint.RoutePattern);
        }
        catch (Exception ex)
        {
            error = $"Route pattern '{endpoint.RoutePattern}' is invalid: {ex.Message}";
            return false;
        }

        return true;
    }

    private static HashSet<string> BuildOccupiedRouteSet(
        IEnumerable<EndpointDataSource> dataSources)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in dataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || methods.Count == 0)
                continue;

            foreach (var method in methods)
                result.Add(RouteKey(method, "/" + endpoint.RoutePattern.RawText?.TrimStart('/')));
        }

        return result;
    }

    private static string RouteKey(string method, string routePattern) =>
        method.ToUpperInvariant() + " " + routePattern.TrimEnd('/');
}
