using System.Net;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Represents a request queued for sequential forwarding to the core API.
/// </summary>
public sealed class QueuedRequest
{
    /// <summary>Unique correlation id for logging.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>HTTP method to forward.</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>Core API relative path (e.g. <c>/agents</c>).</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Serialised JSON body for POST/PUT. <c>null</c> for DELETE.
    /// </summary>
    public string? JsonBody { get; init; }

    /// <summary>Timestamp when the request was enqueued.</summary>
    public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Priority lane. Read from <c>X-Priority</c> header by the dispatcher.
    /// Default: <see cref="RequestPriority.Normal"/>.
    /// </summary>
    public RequestPriority Priority { get; init; } = RequestPriority.Normal;

    /// <summary>
    /// Number of items ahead in the queue when this request was enqueued.
    /// Set by <see cref="RequestQueueService.TryEnqueue"/>.
    /// </summary>
    public int QueuePosition { get; internal set; }

    /// <summary>
    /// Completion source the enqueuing controller awaits.
    /// Set by the processor when the core responds (or fails).
    /// </summary>
    public TaskCompletionSource<QueuedResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The result of a processed <see cref="QueuedRequest"/>.
/// </summary>
public sealed class QueuedResponse
{
    public required HttpStatusCode StatusCode { get; init; }
    public string? JsonBody { get; init; }
    public string? Error { get; init; }

    /// <summary>Queue metadata for response headers. Set by the processor.</summary>
    public QueueResponseMeta? Meta { get; set; }

    public bool IsSuccess => (int)StatusCode >= 200 && (int)StatusCode < 300;
}
