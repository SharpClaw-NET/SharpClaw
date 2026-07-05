namespace SharpClaw.Gateway.Abstractions;

/// <summary>
/// Read-only forwarder a module can inject to perform <c>GET</c> requests
/// against the internal SharpClaw Runtime Host. The implementation
/// attaches the <c>X-Api-Key</c> header, the gateway service token, and
/// forwards the caller's <c>Authorization</c> header unchanged. Mutations
/// must go through <see cref="IGatewayDispatcher"/> instead so they can be
/// queued for sequential processing.
/// </summary>
public interface IGatewayInternalApi
{
    /// <summary>
    /// Forwards a <c>GET</c> request to the internal API and deserialises the
    /// JSON response body into <typeparamref name="T"/>. Returns <c>null</c>
    /// if the response body is empty or represents <c>null</c>.
    /// </summary>
    Task<T?> GetAsync<T>(string path, CancellationToken ct = default);
}
