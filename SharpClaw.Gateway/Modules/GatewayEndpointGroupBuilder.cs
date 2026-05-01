using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using SharpClaw.Gateway.Abstractions;

namespace SharpClaw.Gateway.Modules;

/// <summary>
/// Concrete <see cref="IGatewayEndpointGroupBuilder"/> handed to module code
/// during <see cref="IGatewayModuleExtension.MapEndpoints"/>. Wraps a
/// <see cref="RouteGroupBuilder"/> so the underlying
/// <see cref="IEndpointRouteBuilder"/> never leaks out — modules can only map
/// routes that the gateway has already enrolled in rate limiting and gating.
/// </summary>
internal sealed class GatewayEndpointGroupBuilder(
    RouteGroupBuilder group,
    string groupId,
    string pathPrefix) : IGatewayEndpointGroupBuilder
{
    public string GroupId { get; } = groupId;

    public string PathPrefix { get; } = pathPrefix;

    public RouteHandlerBuilder MapGet(string pattern, Delegate handler)
        => group.MapGet(pattern, handler);

    public RouteHandlerBuilder MapPost(string pattern, Delegate handler)
        => group.MapPost(pattern, handler);

    public RouteHandlerBuilder MapPut(string pattern, Delegate handler)
        => group.MapPut(pattern, handler);

    public RouteHandlerBuilder MapDelete(string pattern, Delegate handler)
        => group.MapDelete(pattern, handler);
}
