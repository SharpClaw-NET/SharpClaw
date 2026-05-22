namespace SharpClaw.Application.Core.Modules.Foreign;

public interface IForeignModuleRuntimeHost : IModuleRuntimeHost
{
    IReadOnlyList<ForeignModuleEndpointDescriptor> Endpoints { get; }

    Task<HttpResponseMessage> SendEndpointRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct = default);

    Task<System.Net.WebSockets.ClientWebSocket> ConnectEndpointWebSocketAsync(
        string pathAndQuery,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        CancellationToken ct = default);
}
