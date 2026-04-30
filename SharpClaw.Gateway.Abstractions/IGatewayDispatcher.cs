namespace SharpClaw.Gateway.Abstractions;

/// <summary>
/// Mutation forwarder a module injects to perform <c>POST</c>/<c>PUT</c>/<c>DELETE</c>
/// requests against the internal SharpClaw Application API. The implementation
/// routes mutations through the gateway's request queue when enabled so that
/// sequential processing, circuit breaking, and queue metrics apply uniformly
/// to module-contributed endpoints.
/// </summary>
public interface IGatewayDispatcher
{
    /// <summary>Enqueues a <c>POST</c> request with a JSON body.</summary>
    Task<QueuedResponse> PostAsync<TRequest>(string path, TRequest body, CancellationToken ct = default);

    /// <summary>Enqueues a body-less <c>POST</c> request.</summary>
    Task<QueuedResponse> PostAsync(string path, CancellationToken ct = default);

    /// <summary>Enqueues a <c>PUT</c> request with a JSON body.</summary>
    Task<QueuedResponse> PutAsync<TRequest>(string path, TRequest body, CancellationToken ct = default);

    /// <summary>Enqueues a <c>DELETE</c> request.</summary>
    Task<QueuedResponse> DeleteAsync(string path, CancellationToken ct = default);
}
