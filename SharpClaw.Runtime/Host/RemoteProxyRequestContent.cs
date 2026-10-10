using System.Net;
using Microsoft.AspNetCore.Http;

namespace SharpClaw.Runtime.Host;

internal sealed class RemoteProxyRequestContent(HttpRequest request) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        request.Body.CopyToAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken) =>
        request.Body.CopyToAsync(stream, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = request.ContentLength.GetValueOrDefault();
        return request.ContentLength.HasValue;
    }
}
