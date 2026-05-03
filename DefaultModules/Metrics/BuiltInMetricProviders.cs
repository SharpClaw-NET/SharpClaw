using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Metrics;

/// <summary>Reports the number of pending (Queued) agent jobs (<c>Queue.PendingJobCount</c>).</summary>
public sealed class PendingJobCountMetricProvider(IHostQueueMetrics host) : ITaskMetricProvider
{
    public string MetricName  => "Queue.PendingJobCount";
    public string Description => "Number of agent jobs currently in the Queued state.";

    public Task<double> GetValueAsync(CancellationToken ct) =>
        host.GetPendingJobCountAsync(ct);
}

/// <summary>Reports the number of pending (Queued) task instances (<c>Queue.PendingTaskCount</c>).</summary>
public sealed class PendingTaskCountMetricProvider(IHostQueueMetrics host) : ITaskMetricProvider
{
    public string MetricName  => "Queue.PendingTaskCount";
    public string Description => "Number of task instances currently in the Queued state.";

    public Task<double> GetValueAsync(CancellationToken ct) =>
        host.GetPendingTaskCountAsync(ct);
}

/// <summary>
/// Reports the number of scheduled jobs with a past <c>NextRunAt</c>
/// that have not yet run (<c>Scheduler.PendingJobCount</c>).
/// </summary>
public sealed class SchedulerPendingJobCountMetricProvider(IHostQueueMetrics host) : ITaskMetricProvider
{
    public string MetricName  => "Scheduler.PendingJobCount";
    public string Description => "Number of scheduled jobs past their NextRunAt time that have not been triggered.";

    public Task<double> GetValueAsync(CancellationToken ct) =>
        host.GetSchedulerPendingJobCountAsync(ct);
}
