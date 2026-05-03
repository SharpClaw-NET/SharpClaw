namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Provides a named, pollable numeric metric value. Hosts and modules can
/// register implementations to expose runtime measurements (queue depths,
/// throughput, latency, custom counters, etc.) to any consumer that needs to
/// observe or compare them — for example trigger sources that fire on
/// threshold crossings, dashboards, or diagnostic endpoints.
/// </summary>
public interface ITaskMetricProvider
{
    /// <summary>
    /// Stable, unique identifier for this metric. Convention is a short,
    /// developer-friendly dotted or kebab-case name (e.g. <c>queue.pending-jobs</c>,
    /// <c>http.request-latency-ms</c>) so it reads well in scripts, logs, and UI.
    /// </summary>
    string MetricName { get; }

    /// <summary>Human-readable description of the metric.</summary>
    string Description { get; }

    /// <summary>Returns the current value of this metric.</summary>
    Task<double> GetValueAsync(CancellationToken ct);
}
