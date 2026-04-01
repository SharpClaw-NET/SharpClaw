namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Consistent JSON error envelope for all gateway responses.
/// Shape: <c>{ "error": "...", "code": "...", "requestId": "..." }</c>
/// </summary>
public static class GatewayErrors
{
    // ── Error codes ──────────────────────────────────────────────

    public const string IpBanned = "IP_BANNED";
    public const string TooManyRequests = "RATE_LIMITED";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string EndpointDisabled = "ENDPOINT_DISABLED";
    public const string GatewayDisabled = "GATEWAY_DISABLED";
    public const string BadGateway = "BAD_GATEWAY";
    public const string QueueFull = "QUEUE_FULL";
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>
    /// Writes a JSON error envelope to the response. Uses <c>WriteAsJsonAsync</c>
    /// which sets <c>Content-Type: application/json</c> automatically.
    /// The <c>requestId</c> field is sourced from <see cref="HttpContext.Items"/>
    /// (set by the telemetry middleware) or a fresh GUID.
    /// </summary>
    public static Task WriteAsync(HttpContext context, int statusCode, string error, string code)
    {
        var requestId = context.Items.TryGetValue("RequestId", out var id) && id is string s
            ? s : Guid.NewGuid().ToString("N");

        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { error, code, requestId });
    }
}
