using System.Collections.Concurrent;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Thread-safe rolling-window metrics for the gateway request queue.
/// Tracks processing durations over the last hour so clients can
/// estimate wait times during high-load periods.
/// </summary>
public sealed class QueueMetrics
{
    private readonly ConcurrentQueue<(long CompletedTicks, double DurationMs)> _completions = new();
    private long _totalEnqueued;
    private long _totalProcessed;

    /// <summary>Records a completed request with its processing duration.</summary>
    public void RecordCompletion(double durationMs)
    {
        Interlocked.Increment(ref _totalProcessed);
        _completions.Enqueue((DateTimeOffset.UtcNow.Ticks, durationMs));
        Prune();
    }

    /// <summary>Increments the total-enqueued counter.</summary>
    public void RecordEnqueue() => Interlocked.Increment(ref _totalEnqueued);

    /// <summary>
    /// Average processing time (ms) for requests completed in the last hour.
    /// Returns <c>0</c> when no data is available.
    /// </summary>
    public double AverageProcessingMs
    {
        get
        {
            Prune();
            var items = _completions.ToArray();
            return items.Length == 0 ? 0 : items.Average(x => x.DurationMs);
        }
    }

    /// <summary>Number of requests completed in the last hour.</summary>
    public int ProcessedLastHour
    {
        get
        {
            Prune();
            return _completions.Count;
        }
    }

    /// <summary>Total requests enqueued since service start.</summary>
    public long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);

    /// <summary>Total requests processed since service start.</summary>
    public long TotalProcessed => Interlocked.Read(ref _totalProcessed);

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1).Ticks;
        while (_completions.TryPeek(out var oldest) && oldest.CompletedTicks < cutoff)
            _completions.TryDequeue(out _);
    }
}

/// <summary>
/// Snapshot of queue metadata attached to a processed request.
/// Stored in <see cref="HttpContext.Items"/> by the dispatcher so the
/// response-header middleware can emit <c>X-Queue-*</c> headers.
/// </summary>
public sealed record QueueResponseMeta(
    Guid RequestId,
    int Position,
    double ProcessingMs,
    double AverageMs);
