namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Host-provided probes for built-in queue and scheduler counts (pending agent
/// jobs, pending task instances, due-but-not-yet-triggered scheduled jobs).
/// Exposed as a contract so any consumer — metric providers, health checks,
/// diagnostic endpoints, dashboards — can read these numbers without taking
/// a direct dependency on the host's persistence layer.
/// </summary>
public interface IHostQueueMetrics
{
    /// <summary>Number of agent jobs currently in the Queued state.</summary>
    Task<double> GetPendingJobCountAsync(CancellationToken ct);

    /// <summary>Number of task instances currently in the Queued state.</summary>
    Task<double> GetPendingTaskCountAsync(CancellationToken ct);

    /// <summary>Number of scheduled jobs past their NextRunAt time that have not been triggered.</summary>
    Task<double> GetSchedulerPendingJobCountAsync(CancellationToken ct);
}
