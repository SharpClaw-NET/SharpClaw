using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Core.Services.Triggers;

/// <summary>
/// Host implementation of <see cref="IHostQueueMetrics"/>. Lives in core because
/// it depends on <see cref="SharpClawDbContext"/> from Application.Infrastructure.
/// Any consumer (metric providers, health checks, dashboards) can read these
/// counts through the contract without referencing the host's persistence layer.
/// </summary>
public sealed class HostQueueMetrics(IServiceProvider services) : IHostQueueMetrics
{
    public async Task<double> GetPendingJobCountAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        return await db.AgentJobs.CountAsync(j => j.Status == AgentJobStatus.Queued, ct);
    }

    public async Task<double> GetPendingTaskCountAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        return await db.TaskInstances.CountAsync(i => i.Status == TaskInstanceStatus.Queued, ct);
    }

    public Task<double> GetSchedulerPendingJobCountAsync(CancellationToken ct)
    {
        // Scheduler ownership has moved to the AgentOrchestration module; the
        // host no longer tracks scheduled jobs. Modules that care can publish
        // their own metric provider against their own DbContext.
        return Task.FromResult(0d);
    }
}
