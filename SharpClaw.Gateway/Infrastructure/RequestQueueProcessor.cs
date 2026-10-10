using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Contracts;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Infrastructure;


/// <summary>
/// Background service that reads from <see cref="RequestQueueService"/>
/// and forwards requests to the core API via <see cref="InternalApiClient"/>,
/// honouring concurrency, timeout, and retry settings.
/// </summary>
internal sealed class RequestQueueProcessor(
    RequestQueueService queue,
    InternalApiClient coreApi,
    IOptions<RequestQueueOptions> options,
    ILogger<RequestQueueProcessor> logger,
    GatewayBackgroundActionBoundary backgroundActions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serviceInvocation = new GatewayBackgroundServiceInvocation(
            "gateway.request-queue");
        Exception? failure = null;
        try
        {
            await backgroundActions.StartAsync(serviceInvocation, stoppingToken).ConfigureAwait(false);

            if (queue.Enabled)
            {
                var opts = options.Value;
                GatewayLog.QueueStarted(logger, opts.MaxConcurrency, opts.TimeoutSeconds,
                    opts.MaxRetries, opts.RetryDelayMs, opts.MaxQueueSize);
                await ProcessQueueAsync(opts, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                GatewayLog.QueueDisabled(logger);
            }
        }
#pragma warning disable CA1031 // Retain the service failure until all owned requests and service.stop settle, then rethrow it.
        catch (Exception exception)
        {
            failure = exception;
        }
#pragma warning restore CA1031
        await StopServiceAsync(serviceInvocation, failure, stoppingToken).ConfigureAwait(false);
    }

    private async Task StopServiceAsync(
        GatewayBackgroundServiceInvocation invocation, Exception? failure, CancellationToken stoppingToken)
    {
        queue.CompleteWaitingRequests(stoppingToken, failure);
        try
        {
            await backgroundActions.StopAsync(invocation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception stopFailure) when (failure is not null)
        {
            throw new AggregateException(failure, stopFailure);
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task ProcessQueueAsync(RequestQueueOptions opts, CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await RunQueueWorkersAsync(opts, session).ConfigureAwait(false);
    }

    private Task RunQueueWorkersAsync(RequestQueueOptions opts, CancellationTokenSource session)
    {
        var workers = new Task[Math.Max(1, opts.MaxConcurrency)];
        foreach (ref var worker in workers.AsSpan())
            worker = RunQueueWorkerAsync(opts, session);

        // Every worker, including one that faults, stays owned until shutdown settles.
        return Task.WhenAll(workers);
    }

    private async Task RunQueueWorkerAsync(RequestQueueOptions opts, CancellationTokenSource session)
    {
        try
        {
            while (!session.IsCancellationRequested)
            {
                var request = await queue.DequeueAsync(session.Token).ConfigureAwait(false);
                await ProcessOwnedTickAsync(request, opts, session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            // Host shutdown has cancelled this worker; its active receipt is already settled.
        }
        catch (Exception failure)
        {
            try
            {
                await session.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception cancellationFailure)
            {
                throw new AggregateException(failure, cancellationFailure);
            }

            throw;
        }
    }

    private async Task ProcessOwnedTickAsync(
        QueuedRequest request, RequestQueueOptions opts, CancellationToken cancellationToken)
    {
        try
        {
            await RunTickAsync(request, opts, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            request.Completion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            request.Completion.TrySetException(exception);
            throw;
        }
    }

    private ValueTask RunTickAsync(
        QueuedRequest request,
        RequestQueueOptions opts,
        CancellationToken cancellationToken) =>
        backgroundActions.ExecuteTickAsync(
            new GatewayBackgroundTickInvocation(
                "gateway.request-queue",
                "request.forward",
                request.Id),
            async ct => await ProcessRequestAsync(request, opts, ct).ConfigureAwait(false),
            cancellationToken);

    private async Task ProcessRequestAsync(
        QueuedRequest request, RequestQueueOptions opts, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var attempt = 0;
        var delay = opts.RetryDelayMs;

        while (true)
        {
            attempt++;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

                var response = await ForwardToCoreAsync(request, cts.Token).ConfigureAwait(false);
                CompleteRequest(request, response, sw, attempt);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    // Application shutdown — cancel the request
                    request.Completion.TrySetCanceled(ct);
                    return;
                }

                if (attempt > opts.MaxRetries)
                {
                    CompleteFailedRequest(request, ex, sw, attempt);
                    return;
                }

                GatewayLog.RetryingRequest(logger, ex, request.Method, request.Path,
                    request.Id, attempt, opts.MaxRetries, delay);

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 10_000); // exponential backoff, cap at 10s
            }
        }
    }

    private void CompleteRequest(QueuedRequest request, QueuedResponse response, Stopwatch elapsed, int attempt)
    {
        elapsed.Stop();
        response.Meta = CreateCompletionMeta(request, elapsed);
        queue.Metrics.RecordCompletion(elapsed.Elapsed.TotalMilliseconds);
        request.Completion.TrySetResult(response);
        if (response.IsSuccess)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                GatewayLog.RequestProcessed(logger, request.Method, request.Path, request.Id,
                    (int)response.StatusCode, elapsed.Elapsed.TotalMilliseconds, attempt);
        }
        else
        {
            GatewayLog.UnsuccessfulRequest(logger, request.Method, request.Path, request.Id,
                (int)response.StatusCode, response.Error);
        }
    }

    private void CompleteFailedRequest(QueuedRequest request, Exception exception, Stopwatch elapsed, int attempt)
    {
        elapsed.Stop();
        queue.Metrics.RecordCompletion(elapsed.Elapsed.TotalMilliseconds);
        var meta = CreateCompletionMeta(request, elapsed);
        if (logger.IsEnabled(LogLevel.Error))
            GatewayLog.RequestFailed(logger, exception, request.Method, request.Path, request.Id,
                attempt, elapsed.Elapsed.TotalMilliseconds);
        request.Completion.TrySetResult(new QueuedResponse
        {
            StatusCode = HttpStatusCode.BadGateway,
            Error = $"Core API unreachable after {attempt} attempts: {exception.Message}",
            Meta = meta,
        });
    }

    private QueueResponseMeta CreateCompletionMeta(QueuedRequest request, Stopwatch elapsed)
    {
        return new QueueResponseMeta(request.Id, request.QueuePosition,
            elapsed.Elapsed.TotalMilliseconds, queue.Metrics.AverageProcessingMs);
    }

    private async Task<QueuedResponse> ForwardToCoreAsync(QueuedRequest request, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(request.Method, request.Path);

        if (request.JsonBody is not null)
        {
            httpRequest.Content = new StringContent(request.JsonBody, System.Text.Encoding.UTF8);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        // Use the InternalApiClient's underlying HttpClient with API key
        using var response = await coreApi.SendRawAsync(httpRequest, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return new QueuedResponse
        {
            StatusCode = response.StatusCode,
            JsonBody = body,
            Error = response.IsSuccessStatusCode ? null : body,
        };
    }
}
