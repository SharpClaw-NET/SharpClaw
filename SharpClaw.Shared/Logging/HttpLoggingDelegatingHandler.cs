using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Shared.Logging;

/// <summary>
/// Emits bounded HTTP metadata only. It never reads request or response
/// bodies and never records headers, query values, or credential material.
/// </summary>
public sealed class HttpLoggingDelegatingHandler(
    ILogger<HttpLoggingDelegatingHandler> logger) : DelegatingHandler
{
    public HttpLoggingDelegatingHandler(
        ILogger<HttpLoggingDelegatingHandler> logger,
        HttpMessageHandler innerHandler)
        : this(logger)
    {
        InnerHandler = innerHandler
            ?? throw new ArgumentNullException(nameof(innerHandler));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var path = SafePath(request.RequestUri);
        logger.LogDebug(
            "HTTP request started: {Method} {Path}; content length={ContentLength}",
            request.Method,
            path,
            request.Content?.Headers.ContentLength);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            logger.LogDebug(
                "HTTP request completed: {StatusCode} after {ElapsedMilliseconds}ms: {Method} {Path}; response length={ContentLength}",
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                request.Method,
                path,
                response.Content?.Headers.ContentLength);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "HTTP request failed after {ElapsedMilliseconds}ms: {Method} {Path}",
                stopwatch.ElapsedMilliseconds,
                request.Method,
                path);
            throw;
        }
    }

    private static string SafePath(Uri? uri)
    {
        if (uri is null)
            return string.Empty;
        return uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
    }
}
