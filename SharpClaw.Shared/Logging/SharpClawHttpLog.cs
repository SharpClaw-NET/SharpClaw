using Microsoft.Extensions.Logging;

namespace SharpClaw.Shared.Logging;

internal static partial class SharpClawHttpLog
{
    [LoggerMessage(0, LogLevel.Debug, "HTTP request started: {Method} {Path}; content length={ContentLength}")]
    public static partial void Started(ILogger logger, HttpMethod method, string path, long? contentLength);

    [LoggerMessage(0, LogLevel.Debug, "HTTP request completed: {StatusCode} after {ElapsedMilliseconds}ms: {Method} {Path}; response length={ContentLength}")]
    public static partial void Completed(
        ILogger logger, int statusCode, long elapsedMilliseconds, HttpMethod method, string path, long? contentLength);

    [LoggerMessage(0, LogLevel.Error, "HTTP request failed after {ElapsedMilliseconds}ms: {Method} {Path}")]
    public static partial void Failed(
        ILogger logger, Exception exception, long elapsedMilliseconds, HttpMethod method, string path);
}
