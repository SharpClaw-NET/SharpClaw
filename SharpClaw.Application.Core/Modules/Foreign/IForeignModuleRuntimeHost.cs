namespace SharpClaw.Application.Core.Modules.Foreign;

public interface IForeignModuleRuntimeHost : IModuleRuntimeHost
{
    IReadOnlyList<ForeignModuleEndpointDescriptor> Endpoints { get; }

    Task<HttpResponseMessage> SendEndpointRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct = default);
}
