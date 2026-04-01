using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Singleton service that accepts mutation requests from controllers,
/// buffers them in a priority queue, and processes them by priority
/// (highest first, FIFO within the same priority) against the core API.
/// <para>
/// GET requests bypass this queue entirely — they are forwarded directly
/// by controllers via <see cref="InternalApiClient"/>.
/// </para>
/// </summary>
public sealed class RequestQueueService : IDisposable
{
    private readonly PriorityQueue<QueuedRequest, (int Priority, long Sequence)> _queue = new();
    private readonly SemaphoreSlim _signal;
    private readonly Lock _lock = new();
    private readonly ILogger<RequestQueueService> _logger;
    private readonly RequestQueueOptions _options;
    private long _sequence;
    private int _count;
    private bool _disposed;

    public RequestQueueService(
        IOptions<RequestQueueOptions> options,
        QueueMetrics metrics,
        ILogger<RequestQueueService> logger)
    {
        _options = options.Value;
        _logger = logger;
        Metrics = metrics;
        _signal = new SemaphoreSlim(0, _options.MaxQueueSize);
    }

    /// <summary>Whether the queue feature is enabled.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>Processing metrics for the last hour.</summary>
    public QueueMetrics Metrics { get; }

    /// <summary>Current number of items waiting in the queue.</summary>
    public int PendingCount
    {
        get { lock (_lock) { return _count; } }
    }

    /// <summary>
    /// Enqueues a mutation request. Returns immediately with a
    /// <see cref="QueuedRequest"/> whose <see cref="QueuedRequest.Completion"/>
    /// the caller awaits for the result.
    /// </summary>
    /// <returns><c>true</c> if enqueued; <c>false</c> if the queue is full.</returns>
    public bool TryEnqueue(QueuedRequest request)
    {
        lock (_lock)
        {
            if (_count >= _options.MaxQueueSize)
            {
                _logger.LogWarning("Request queue full. Rejecting {Method} {Path}.", request.Method, request.Path);
                return false;
            }

            request.QueuePosition = _count;
            _queue.Enqueue(request, ((int)request.Priority, ++_sequence));
            _count++;
        }

        _signal.Release();
        Metrics.RecordEnqueue();

        _logger.LogDebug("Enqueued {Method} {Path} ({Id}) [{Priority}]. Position: {Position}, Pending: {Count}.",
            request.Method, request.Path, request.Id, request.Priority, request.QueuePosition, PendingCount);

        return true;
    }

    /// <summary>
    /// Dequeues the highest-priority request (FIFO within the same priority).
    /// Blocks asynchronously until an item is available or cancellation is requested.
    /// </summary>
    public async Task<QueuedRequest> DequeueAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct);

        lock (_lock)
        {
            _count--;
            return _queue.Dequeue();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _signal.Dispose();
    }
}

/// <summary>
/// Background service that reads from <see cref="RequestQueueService"/>
/// and forwards requests to the core API via <see cref="InternalApiClient"/>,
/// honouring concurrency, timeout, and retry settings.
/// </summary>
public sealed class RequestQueueProcessor(
    RequestQueueService queue,
    InternalApiClient coreApi,
    IOptions<RequestQueueOptions> options,
    ILogger<RequestQueueProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!queue.Enabled)
        {
            logger.LogInformation("Request queue is disabled — processor will not start.");
            return;
        }

        var opts = options.Value;
        logger.LogInformation(
            "Request queue processor started. Concurrency={Concurrency}, Timeout={Timeout}s, " +
            "MaxRetries={MaxRetries}, RetryDelay={RetryDelay}ms, QueueCapacity={Capacity}.",
            opts.MaxConcurrency, opts.TimeoutSeconds, opts.MaxRetries, opts.RetryDelayMs, opts.MaxQueueSize);

        if (opts.MaxConcurrency <= 1)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var request = await queue.DequeueAsync(stoppingToken);
                await ProcessRequestAsync(request, opts, stoppingToken);
            }
        }
        else
        {
            using var semaphore = new SemaphoreSlim(opts.MaxConcurrency, opts.MaxConcurrency);
            var tasks = new List<Task>();

            while (!stoppingToken.IsCancellationRequested)
            {
                var request = await queue.DequeueAsync(stoppingToken);
                await semaphore.WaitAsync(stoppingToken);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessRequestAsync(request, opts, stoppingToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, stoppingToken));

                tasks.RemoveAll(t => t.IsCompleted);
            }

            await Task.WhenAll(tasks);
        }
    }

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

                var response = await ForwardToCoreAsync(request, cts.Token);
                sw.Stop();

                response.Meta = new QueueResponseMeta(
                    request.Id,
                    request.QueuePosition,
                    sw.Elapsed.TotalMilliseconds,
                    queue.Metrics.AverageProcessingMs);

                queue.Metrics.RecordCompletion(sw.Elapsed.TotalMilliseconds);
                request.Completion.TrySetResult(response);

                if (response.IsSuccess)
                {
                    logger.LogDebug("Processed {Method} {Path} ({Id}) → {Status} in {Ms:F0}ms on attempt {Attempt}.",
                        request.Method, request.Path, request.Id, (int)response.StatusCode, sw.Elapsed.TotalMilliseconds, attempt);
                }
                else
                {
                    logger.LogWarning("Processed {Method} {Path} ({Id}) → {Status}: {Error}.",
                        request.Method, request.Path, request.Id, (int)response.StatusCode, response.Error);
                }
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
                    sw.Stop();
                    queue.Metrics.RecordCompletion(sw.Elapsed.TotalMilliseconds);

                    logger.LogError(ex, "Failed {Method} {Path} ({Id}) after {Attempts} attempts in {Ms:F0}ms.",
                        request.Method, request.Path, request.Id, attempt, sw.Elapsed.TotalMilliseconds);

                    request.Completion.TrySetResult(new QueuedResponse
                    {
                        StatusCode = HttpStatusCode.BadGateway,
                        Error = $"Core API unreachable after {attempt} attempts: {ex.Message}",
                        Meta = new QueueResponseMeta(
                            request.Id,
                            request.QueuePosition,
                            sw.Elapsed.TotalMilliseconds,
                            queue.Metrics.AverageProcessingMs),
                    });
                    return;
                }

                logger.LogWarning(ex,
                    "Transient failure on {Method} {Path} ({Id}), attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms.",
                    request.Method, request.Path, request.Id, attempt, opts.MaxRetries, delay);

                await Task.Delay(delay, ct);
                delay = Math.Min(delay * 2, 10_000); // exponential backoff, cap at 10s
            }
        }
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
        var response = await coreApi.SendRawAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        return new QueuedResponse
        {
            StatusCode = response.StatusCode,
            JsonBody = body,
            Error = response.IsSuccessStatusCode ? null : body,
        };
    }
}
