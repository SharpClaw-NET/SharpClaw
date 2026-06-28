using Microsoft.Extensions.DependencyInjection;

namespace SharpClaw.Gateway.Abstractions;

/// <summary>
/// Implemented by a module class (or a sibling class in the same assembly)
/// to publish a slice of the module's surface through the SharpClaw gateway
/// process. The gateway loader discovers <see cref="IGatewayModuleExtension"/>
/// instances independently of <c>ISharpClawCoreModule</c>; a module that has no
/// public surface simply does not implement this contract.
/// </summary>
public interface IGatewayModuleExtension
{
    /// <summary>
    /// Stable module identifier used both as the URL segment
    /// (<c>/api/modules/{ModuleId}/...</c>) and as the configuration key
    /// (<c>Gateway:Modules:{ModuleId}:Enabled</c>).
    /// </summary>
    string ModuleId { get; }

    /// <summary>Human-readable name surfaced in operator tooling and OpenAPI metadata.</summary>
    string DisplayName { get; }

    /// <summary>
    /// The endpoint groups this module contributes. Each group becomes a
    /// rate-limited, individually toggleable route group beneath
    /// <c>/api/modules/{ModuleId}/</c>.
    /// </summary>
    IReadOnlyList<GatewayEndpointGroup> GetEndpointGroups();

    /// <summary>
    /// Optional hook to register gateway-process-local services. The gateway
    /// container is independent of the API container; modules must not assume
    /// any core service is available here. Default implementation is a no-op.
    /// </summary>
    void ConfigureGatewayServices(IServiceCollection services) { }

    /// <summary>
    /// Maps the module's routes through the supplied
    /// <see cref="IGatewayEndpointGroupBuilder"/> wrapper, which enrolls each
    /// route in the gateway's rate-limit and gating pipeline.
    /// </summary>
    void MapEndpoints(IGatewayEndpointGroupBuilder builder);
}
