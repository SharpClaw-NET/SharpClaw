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
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var path = SafePath(request.RequestUri);
        if (logger.IsEnabled(LogLevel.Debug))
            SharpClawHttpLog.Started(logger, request.Method, path, request.Content?.Headers.ContentLength);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if (logger.IsEnabled(LogLevel.Debug))
                SharpClawHttpLog.Completed(logger, (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds, request.Method, path,
                    response.Content?.Headers.ContentLength);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (logger.IsEnabled(LogLevel.Error))
                SharpClawHttpLog.Failed(logger, ex, stopwatch.ElapsedMilliseconds, request.Method, path);
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
