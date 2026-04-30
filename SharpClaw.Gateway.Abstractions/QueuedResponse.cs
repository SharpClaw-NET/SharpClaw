using System.Net;

namespace SharpClaw.Gateway.Abstractions;

/// <summary>
/// The result of a processed gateway request, returned by
/// <see cref="IGatewayDispatcher"/> mutation methods and by the internal
/// request-queue processor. Lives in the abstractions assembly so module
/// code can inspect status, body, and queue metadata without taking a
/// reference on the gateway implementation.
/// </summary>
public sealed class QueuedResponse
{
    /// <summary>HTTP status code returned by the internal API (or by the gateway when failing fast).</summary>
    public required HttpStatusCode StatusCode { get; init; }

    /// <summary>Raw JSON body returned by the internal API. <c>null</c> when the response had no body.</summary>
    public string? JsonBody { get; init; }

    /// <summary>Error description set by the gateway when the response was not a success.</summary>
    public string? Error { get; init; }

    /// <summary>Queue metadata for response headers. Set by the queue processor.</summary>
    public QueueResponseMeta? Meta { get; set; }

    /// <summary>True when <see cref="StatusCode"/> is in the 2xx range.</summary>
    public bool IsSuccess => (int)StatusCode >= 200 && (int)StatusCode < 300;
}

/// <summary>
/// Snapshot of queue metadata attached to a processed request. The gateway's
/// response-header middleware reads this from the <see cref="QueuedResponse"/>
/// and emits the corresponding <c>X-Queue-*</c> headers.
/// </summary>
public sealed record QueueResponseMeta(
    Guid RequestId,
    int Position,
    double ProcessingMs,
    double AverageMs);
