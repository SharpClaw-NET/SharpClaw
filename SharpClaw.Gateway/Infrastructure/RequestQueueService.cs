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
/// Singleton service that accepts mutation requests from controllers,
/// buffers them in a priority queue, and processes them by priority
/// (highest first, FIFO within the same priority) against the core API.
/// <para>
/// GET requests bypass this queue entirely — they are forwarded directly
/// by controllers via <see cref="InternalApiClient"/>.
/// </para>
/// </summary>
internal sealed class RequestQueueService : IDisposable
{
    private readonly PriorityQueue<QueuedRequest, (int Priority, long Sequence)> _queue = new();
    private readonly SemaphoreSlim _signal;
    private readonly Lock _lock = new();
    private readonly ILogger<RequestQueueService> _logger;
    private readonly RequestQueueOptions _options;
    private long _sequence;
    private int _count;
    private bool _disposed;
    private bool _completed;

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
            if (_completed || _disposed)
                return false;

            if (_count >= _options.MaxQueueSize)
            {
                GatewayLog.QueueFull(_logger, request.Method, request.Path);
                return false;
            }

            request.QueuePosition = _count;
            _queue.Enqueue(request, ((int)request.Priority, ++_sequence));
            _count++;
            _signal.Release();
        }

        Metrics.RecordEnqueue();

        if (_logger.IsEnabled(LogLevel.Debug))
            GatewayLog.RequestEnqueued(_logger, request.Method, request.Path, request.Id,
                request.Priority, request.QueuePosition, PendingCount);

        return true;
    }

    /// <summary>
    /// Dequeues the highest-priority request (FIFO within the same priority).
    /// Blocks asynchronously until an item is available or cancellation is requested.
    /// </summary>
    public async Task<QueuedRequest> DequeueAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct).ConfigureAwait(false);

        lock (_lock)
        {
            _count--;
            return _queue.Dequeue();
        }
    }

    internal void CompleteWaitingRequests(CancellationToken cancellationToken, Exception? failure = null)
    {
        lock (_lock)
        {
            _completed = true;
            var cancelled = cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true);
            while (_queue.TryDequeue(out var request, out _))
            {
                if (failure is null)
                    request.Completion.TrySetCanceled(cancelled);
                else
                    request.Completion.TrySetException(failure);
            }
            _count = 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CompleteWaitingRequests(new CancellationToken(canceled: true));
        _signal.Dispose();
    }
}
