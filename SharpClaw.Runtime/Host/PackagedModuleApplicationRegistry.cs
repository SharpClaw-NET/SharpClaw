using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.ModuleHost.OutOfProcess;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;

internal sealed class PackagedModuleApplicationRegistry
{
    private static readonly HashSet<string> ReservedCliNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "help",
            "--help",
            "-h",
            "chat",
        };

    private static readonly HashSet<string> FilteredRequestHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Connection",
            "Content-Length",
            "Cookie",
            "Host",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "Sec-WebSocket-Accept",
            "Sec-WebSocket-Key",
            "Sec-WebSocket-Protocol",
            "Sec-WebSocket-Version",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade",
            "X-Api-Key",
        };

    private static readonly HashSet<string> FilteredResponseHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Connection",
            "Content-Length",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade",
        };

    private static readonly TimeSpan CarrierLifetime = TimeSpan.FromMinutes(3);
    private readonly IReadOnlyDictionary<string, CliRoute> _cliRoutes;
    private readonly IReadOnlyList<EndpointRoute> _endpointRoutes;

    public static PackagedModuleApplicationRegistry Empty { get; } = new([]);

    public PackagedModuleApplicationRegistry(
        IReadOnlyList<OutOfProcessModuleProxy> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _cliRoutes = BuildCliRoutes(modules);
        _endpointRoutes = BuildEndpointRoutes(modules);
    }

    public async ValueTask<ModuleCliResult?> TryInvokeCliAsync(
        string command,
        IReadOnlyList<string> arguments,
        RuntimeKernelAdapter runtimeKernel,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runtimeKernel);
        ArgumentNullException.ThrowIfNull(executionContext);
        if (!_cliRoutes.TryGetValue(command, out var route))
            return null;

        if (route.Descriptor.RequiresAdministrator && !IsAdministrator(executionContext.Caller))
        {
            return new ModuleCliResult(
                false,
                [],
                new ExecutionError(
                    "administrator_required",
                    "The module command requires administrator authority."));
        }

        var invocation = new RuntimeCliActionInvocation(
            "execute",
            route.Descriptor.Name,
            arguments.Count);
        var descriptor = GetTransportDescriptor(
            runtimeKernel,
            RuntimeCliActionCatalog.Execute);
        var context = route.Client.IssueHostActionContext(
            HostActionEntryIngress.Cli,
            route.Descriptor.Name,
            route.Client.Discovery.ModuleId,
            descriptor,
            new KernelActionEnvelope(descriptor.Key, invocation),
            executionContext.Caller,
            executionContext.Features,
            executionContext.TraceId,
            executionContext.IdempotencyKey,
            DateTimeOffset.UtcNow.Add(CarrierLifetime));
        var response = await route.Client.InvokeCliAsync(
            route.Descriptor.Name,
            arguments,
            context,
            cancellationToken);
        return response.Result;
    }

    public void MapEndpoints(WebApplication app, RuntimeKernelAdapter runtimeKernel)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(runtimeKernel);
        foreach (var route in _endpointRoutes)
        {
            app.MapMethods(
                route.Path,
                [route.Method],
                context => InvokeEndpointRouteAsync(context, route, runtimeKernel));
        }
    }

    private static async Task InvokeEndpointRouteAsync(
        HttpContext context,
        EndpointRoute route,
        RuntimeKernelAdapter runtimeKernel)
    {
        var target = context.WebSockets.IsWebSocketRequest
            ? route.WebSocket
            : route.Http;
        if (target is null)
        {
            context.Response.StatusCode = context.WebSockets.IsWebSocketRequest
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            return;
        }

        var maximumBodyBytes = target.Client.HostLimits.ActionInputBytes;
        if (context.Request.ContentLength > maximumBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var body = await ReadBodyAsync(
            context.Request.Body,
            maximumBodyBytes,
            context.RequestAborted);
        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var executionContext = KernelHostEndpoints.CreateExecutionContext(context);
        var original = new ModuleEndpointIngress(
            target.Descriptor.Id,
            target.Descriptor.Method,
            target.Descriptor.Path,
            CopyHeaders(context.Request.Headers),
            context.Request.Query.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(value => value ?? string.Empty).ToArray(),
                StringComparer.Ordinal),
            context.Request.RouteValues
                .Where(pair => pair.Value is not null)
                .ToDictionary(
                    pair => pair.Key,
                    pair => new[] { pair.Value!.ToString()! },
                    StringComparer.Ordinal),
            body);

        if (target.Descriptor.Transport == HostEndpointTransport.WebSocket)
        {
            await runtimeKernel.RunRequestAsync(
                executionContext,
                original,
                async (effective, cancellationToken) =>
                {
                    ValidateImmutableRoute(original, effective);
                    using var socket = await context.WebSockets.AcceptWebSocketAsync();
                    var channel = new AspNetModuleWebSocketChannel(
                        socket,
                        target.Client.HostLimits.StreamChunkBytes);
                    var request = CreateEndpointRequest(
                        target,
                        effective,
                        executionContext,
                        runtimeKernel);
                    await target.Client.InvokeWebSocketEndpointAsync(
                        request,
                        channel,
                        cancellationToken);
                    return true;
                },
                context.RequestAborted);
            return;
        }

        var response = await runtimeKernel.RunRequestAsync(
            executionContext,
            original,
            (effective, cancellationToken) =>
            {
                ValidateImmutableRoute(original, effective);
                var request = CreateEndpointRequest(
                    target,
                    effective,
                    executionContext,
                    runtimeKernel);
                return target.Client.InvokeEndpointAsync(request, cancellationToken);
            },
            context.RequestAborted);
        context.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers)
        {
            if (!FilteredResponseHeaders.Contains(header.Key))
                context.Response.Headers[header.Key] = new StringValues(header.Value);
        }
        if (response.Body.Length > 0)
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }

    private static HostEndpointRouteRequest CreateEndpointRequest(
        EndpointTarget target,
        ModuleEndpointIngress ingress,
        KernelActionExecutionContext executionContext,
        RuntimeKernelAdapter runtimeKernel)
    {
        var descriptor = GetTransportDescriptor(
            runtimeKernel,
            new SharpClawActionKey("runtime.request.receive"));
        var hostContext = target.Client.IssueHostActionContext(
            HostActionEntryIngress.Endpoint,
            target.Descriptor.Id,
            target.Client.Discovery.ModuleId,
            descriptor,
            new KernelActionEnvelope(descriptor.Key, ingress),
            executionContext.Caller,
            executionContext.Features,
            executionContext.TraceId,
            executionContext.IdempotencyKey,
            DateTimeOffset.UtcNow.Add(CarrierLifetime));
        return new HostEndpointRouteRequest(
            target.Client.CreateEndpointInvocation(target.Descriptor, hostContext),
            target.Descriptor.ToRouteIdentity(),
            FilterHeaders(ingress.Headers),
            ingress.Query,
            ingress.Body)
        {
            RouteValues = ingress.RouteValues,
        };
    }

    private static ActionDescriptor<KernelActionEnvelope, object> GetTransportDescriptor(
        RuntimeKernelAdapter runtimeKernel,
        SharpClawActionKey actionKey)
    {
        var descriptor = runtimeKernel.Graph.GetStandardAction(actionKey);
        var contract = KernelActionCatalog.DescriptorFor(actionKey);
        return descriptor with
        {
            InputSchema = contract.InputSchema,
            ResultSchema = contract.ResultSchema,
        };
    }

    private static IReadOnlyDictionary<string, CliRoute> BuildCliRoutes(
        IReadOnlyList<OutOfProcessModuleProxy> modules)
    {
        var routes = new Dictionary<string, CliRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            foreach (var contribution in module.Client.Application.CliCommands)
            {
                AddCliRoute(routes, contribution.Descriptor.Name, module.Client, contribution.Descriptor);
                foreach (var alias in contribution.Descriptor.Aliases)
                    AddCliRoute(routes, alias, module.Client, contribution.Descriptor);
            }
        }
        return routes;
    }

    private static void AddCliRoute(
        IDictionary<string, CliRoute> routes,
        string name,
        OutOfProcessModuleClient client,
        ModuleCliCommandDescriptor descriptor)
    {
        if (ReservedCliNames.Contains(name) || !routes.TryAdd(name, new CliRoute(client, descriptor)))
        {
            throw new InvalidOperationException(
                $"The module CLI name or alias '{name}' conflicts with another command.");
        }
    }

    private static IReadOnlyList<EndpointRoute> BuildEndpointRoutes(
        IReadOnlyList<OutOfProcessModuleProxy> modules)
    {
        var targets = modules
            .SelectMany(module => module.Client.Application.Endpoints.Select(
                endpoint => new EndpointTarget(module.Client, endpoint.Descriptor)))
            .ToArray();
        var duplicate = targets
            .GroupBy(target => (
                target.Descriptor.Path,
                target.Descriptor.Method,
                target.Descriptor.Transport))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"The module endpoint '{duplicate.Key.Method} {duplicate.Key.Path}' "
                + $"for transport '{duplicate.Key.Transport}' is declared more than once.");
        }

        return targets
            .GroupBy(target => (target.Descriptor.Path, target.Descriptor.Method))
            .Select(group => new EndpointRoute(
                group.Key.Path,
                group.Key.Method,
                group.SingleOrDefault(target =>
                    target.Descriptor.Transport == HostEndpointTransport.Http),
                group.SingleOrDefault(target =>
                    target.Descriptor.Transport == HostEndpointTransport.WebSocket)))
            .OrderBy(route => route.Path, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string[]> CopyHeaders(IHeaderDictionary headers) =>
        headers
            .Where(pair => !FilteredRequestHeaders.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(value => value ?? string.Empty).ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string[]> FilterHeaders(
        IReadOnlyDictionary<string, string[]> headers) =>
        headers
            .Where(pair => !FilteredRequestHeaders.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

    private static async ValueTask<byte[]?> ReadBodyAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[Math.Min(Math.Max(maximumBytes, 1), 64 * 1024)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return body.ToArray();
            if (body.Length + read > maximumBytes)
                return null;
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidateImmutableRoute(
        ModuleEndpointIngress original,
        ModuleEndpointIngress effective)
    {
        if (!string.Equals(original.EndpointId, effective.EndpointId, StringComparison.Ordinal)
            || !string.Equals(original.Method, effective.Method, StringComparison.Ordinal)
            || !string.Equals(original.Path, effective.Path, StringComparison.Ordinal))
        {
            throw new KernelActionExecutionException(
                "The Runtime request action cannot replace module endpoint authority.");
        }
    }

    private static bool IsAdministrator(RequestPrincipal caller) =>
        caller.IsAuthenticated
        && caller.Roles?.Any(role => string.Equals(
            role,
            "administrator",
            StringComparison.OrdinalIgnoreCase)) == true;

    private sealed record CliRoute(
        OutOfProcessModuleClient Client,
        ModuleCliCommandDescriptor Descriptor);

    private sealed record EndpointTarget(
        OutOfProcessModuleClient Client,
        ModuleEndpointRouteDescriptor Descriptor);

    private sealed record EndpointRoute(
        string Path,
        string Method,
        EndpointTarget? Http,
        EndpointTarget? WebSocket);

    private sealed record ModuleEndpointIngress(
        string EndpointId,
        string Method,
        string Path,
        IReadOnlyDictionary<string, string[]> Headers,
        IReadOnlyDictionary<string, string[]> Query,
        IReadOnlyDictionary<string, string[]> RouteValues,
        byte[] Body);

    private sealed class AspNetModuleWebSocketChannel(
        WebSocket socket,
        int maximumMessageBytes) : IModuleWebSocketChannel
    {
        public async ValueTask<ModuleWebSocketMessage?> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            if (socket.State is WebSocketState.Closed or WebSocketState.Aborted)
                return null;

            using var payload = new MemoryStream(Math.Min(maximumMessageBytes, 64 * 1024));
            var buffer = new byte[Math.Min(Math.Max(maximumMessageBytes, 1), 64 * 1024)];
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new ModuleWebSocketMessage(
                        ModuleWebSocketMessageType.Close,
                        [],
                        (int)(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure),
                        result.CloseStatusDescription);
                }
                if (payload.Length + result.Count > maximumMessageBytes)
                    throw new InvalidOperationException("The WebSocket message exceeds the host limit.");
                await payload.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }
            while (!result.EndOfMessage);

            return new ModuleWebSocketMessage(
                result.MessageType == WebSocketMessageType.Text
                    ? ModuleWebSocketMessageType.Text
                    : ModuleWebSocketMessageType.Binary,
                payload.ToArray());
        }

        public ValueTask SendAsync(
            ModuleWebSocketMessage message,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (!message.IsWellFormed || message.Payload.Length > maximumMessageBytes)
                throw new InvalidOperationException("The module WebSocket message is invalid.");
            if (message.Type == ModuleWebSocketMessageType.Close)
            {
                return CloseAsync(
                    message.CloseStatus!.Value,
                    message.CloseDescription,
                    cancellationToken);
            }

            return new ValueTask(socket.SendAsync(
                message.Payload,
                message.Type == ModuleWebSocketMessageType.Text
                    ? WebSocketMessageType.Text
                    : WebSocketMessageType.Binary,
                true,
                cancellationToken));
        }

        public async ValueTask CloseAsync(
            int closeStatus,
            string? description,
            CancellationToken cancellationToken)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    (WebSocketCloseStatus)closeStatus,
                    description,
                    cancellationToken);
            }
        }
    }
}
