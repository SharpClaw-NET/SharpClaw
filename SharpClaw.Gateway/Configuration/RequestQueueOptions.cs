namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Configuration for the gateway request queue that buffers mutation
/// requests before forwarding them to the core API.
/// Loaded from the <c>Gateway:RequestQueue</c> configuration section.
/// </summary>
public sealed class RequestQueueOptions
{
    public const string SectionName = "Gateway:RequestQueue";

    /// <summary>
    /// Maximum number of requests processed concurrently.
    /// Each concurrent slot processes its items sequentially.
    /// Default: 1 (fully sequential).
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Timeout in seconds for a single forwarded request to the core API.
    /// Default: 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of retry attempts for transient failures
    /// (5xx, timeout, connection refused). Default: 2.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Initial delay in milliseconds between retries (doubles each attempt).
    /// Default: 500.
    /// </summary>
    public int RetryDelayMs { get; set; } = 500;

    /// <summary>
    /// Maximum number of requests that can be queued before new
    /// requests are rejected with <c>503 Service Unavailable</c>.
    /// Default: 500.
    /// </summary>
    public int MaxQueueSize { get; set; } = 500;

    /// <summary>
    /// Whether the request queue is enabled. When <c>false</c>,
    /// controllers call the core API directly (bypass mode).
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
