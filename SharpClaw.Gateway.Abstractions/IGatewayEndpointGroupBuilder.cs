using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SharpClaw.Gateway.Abstractions;

/// <summary>
/// Opinionated wrapper over <see cref="IEndpointRouteBuilder"/> handed to a
/// module's <see cref="IGatewayModuleExtension.MapEndpoints"/> implementation.
/// The wrapper is the only path into the gateway's route table for module
/// code: the underlying builder, the rate limiter, and the request queue are
/// all attached by the gateway, so modules cannot accidentally bypass them.
/// </summary>
public interface IGatewayEndpointGroupBuilder
{
    /// <summary>The owning group's identifier (<see cref="GatewayEndpointGroup.GroupId"/>).</summary>
    string GroupId { get; }

    /// <summary>
    /// Absolute path prefix every mapped route is rooted under
    /// (<c>/api/modules/{ModuleId}/{slug}</c>).
    /// </summary>
    string PathPrefix { get; }

    /// <summary>Maps a <c>GET</c> route relative to <see cref="PathPrefix"/>.</summary>
    RouteHandlerBuilder MapGet(string pattern, Delegate handler);

    /// <summary>Maps a <c>POST</c> route relative to <see cref="PathPrefix"/>.</summary>
    RouteHandlerBuilder MapPost(string pattern, Delegate handler);

    /// <summary>Maps a <c>PUT</c> route relative to <see cref="PathPrefix"/>.</summary>
    RouteHandlerBuilder MapPut(string pattern, Delegate handler);

    /// <summary>Maps a <c>DELETE</c> route relative to <see cref="PathPrefix"/>.</summary>
    RouteHandlerBuilder MapDelete(string pattern, Delegate handler);
}
