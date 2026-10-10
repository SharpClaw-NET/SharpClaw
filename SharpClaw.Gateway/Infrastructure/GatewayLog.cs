namespace SharpClaw.Gateway.Infrastructure;

internal static partial class GatewayLog
{
    [LoggerMessage(0, LogLevel.Error, "Unhandled exception in {Controller}/{Action}.")]
    public static partial void UnhandledControllerError(
        ILogger logger, Exception exception, object? controller, object? action);

    [LoggerMessage(0, LogLevel.Error, "Gateway action boundary failed for {Method} {Path}.")]
    public static partial void ActionBoundaryFailed(
        ILogger logger, Exception exception, string method, PathString path);

    [LoggerMessage(0, LogLevel.Trace, "Gateway stream response wrapper disposed after {ChunkCount} chunks.")]
    public static partial void StreamDisposed(ILogger logger, int chunkCount);

    [LoggerMessage(0, LogLevel.Warning, "Runtime returned unauthorized for {Method} {Path}; attempting credential refresh.")]
    public static partial void UnauthorizedRuntime(ILogger logger, HttpMethod method, string path);

    [LoggerMessage(0, LogLevel.Debug, "Attached internal credentials for {Method} {Path}; gateway token present={GatewayTokenPresent}.")]
    public static partial void CredentialsAttached(
        ILogger logger, HttpMethod method, string path, bool gatewayTokenPresent);

    [LoggerMessage(0, LogLevel.Warning, "Direct forward failed for {Method} {Path}.")]
    public static partial void DirectForwardFailed(
        ILogger logger, Exception exception, HttpMethod method, string path);

    [LoggerMessage(0, LogLevel.Warning, "Request queue full. Rejecting {Method} {Path}.")]
    public static partial void QueueFull(ILogger logger, HttpMethod method, string path);

    [LoggerMessage(0, LogLevel.Debug, "Enqueued {Method} {Path} ({Id}) [{Priority}]. Position: {Position}, Pending: {Count}.")]
    public static partial void RequestEnqueued(
        ILogger logger, HttpMethod method, string path, Guid id, RequestPriority priority, int position, int count);

    [LoggerMessage(0, LogLevel.Information, "Request queue is disabled — processor will not start.")]
    public static partial void QueueDisabled(ILogger logger);

    [LoggerMessage(0, LogLevel.Information, "Request queue processor started. Concurrency={Concurrency}, Timeout={Timeout}s, MaxRetries={MaxRetries}, RetryDelay={RetryDelay}ms, QueueCapacity={Capacity}.")]
    public static partial void QueueStarted(
        ILogger logger, int concurrency, int timeout, int maxRetries, int retryDelay, int capacity);

    [LoggerMessage(0, LogLevel.Debug, "Processed {Method} {Path} ({Id}) → {Status} in {Ms:F0}ms on attempt {Attempt}.")]
    public static partial void RequestProcessed(
        ILogger logger, HttpMethod method, string path, Guid id, int status, double ms, int attempt);

    [LoggerMessage(0, LogLevel.Warning, "Processed {Method} {Path} ({Id}) → {Status}: {Error}.")]
    public static partial void UnsuccessfulRequest(
        ILogger logger, HttpMethod method, string path, Guid id, int status, string? error);

    [LoggerMessage(0, LogLevel.Error, "Failed {Method} {Path} ({Id}) after {Attempts} attempts in {Ms:F0}ms.")]
    public static partial void RequestFailed(
        ILogger logger, Exception exception, HttpMethod method, string path, Guid id, int attempts, double ms);

    [LoggerMessage(0, LogLevel.Warning, "Transient failure on {Method} {Path} ({Id}), attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms.")]
    public static partial void RetryingRequest(
        ILogger logger, Exception exception, HttpMethod method, string path, Guid id, int attempt, int maxRetries, int delay);

    [LoggerMessage(0, LogLevel.Warning, "Anti-spam: oversized body from {Ip} ({Bytes} bytes)")]
    public static partial void OversizedBody(ILogger logger, string ip, long? bytes);
}
