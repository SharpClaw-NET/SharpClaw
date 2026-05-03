using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration.Models;

namespace SharpClaw.Modules.AgentOrchestration.Services;

/// <summary>
/// Module-owned scheduler worker. Polls module-side <see cref="ScheduledJobDB"/>
/// rows and dispatches their bound task definitions through the host-supplied
/// <see cref="ITaskInstanceLauncher"/> contract. Started by the module's
/// <c>InitializeAsync</c>; stopped by <c>ShutdownAsync</c>.
/// </summary>
public sealed class ScheduledJobWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ScheduledJobWorker> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _cts = new();
    private Task? _runner;

    public void Start()
    {
        if (_runner is not null) return;
        _runner = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_runner is null) return;
        try { _cts.Cancel(); } catch { }
        try { await _runner.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ScheduledJobWorker shutdown produced an exception.");
        }
        _runner = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        logger.LogInformation("ScheduledJobWorker started.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ProcessDueJobsAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error in scheduled job processing loop.");
                }

                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    internal async Task ProcessDueJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentOrchestrationDbContext>();

        var now = DateTimeOffset.UtcNow;

        var dueJobs = await db.ScheduledJobs
            .Where(t => t.Status == ScheduledTaskStatus.Pending && t.NextRunAt <= now)
            .ToListAsync(ct);

        var missedThreshold = TimeSpan.FromMinutes(
            configuration.GetValue("Scheduler:MissedFireThresholdMinutes", 60));

        foreach (var job in dueJobs)
        {
            job.Status = ScheduledTaskStatus.Running;
            job.LastRunAt = now;
            await db.SaveChangesAsync(ct);

            try
            {
                logger.LogInformation("Executing scheduled job {Name} ({Id}).", job.Name, job.Id);

                if (job.TaskDefinitionId.HasValue)
                {
                    var launcher = scope.ServiceProvider.GetRequiredService<ITaskInstanceLauncher>();

                    Dictionary<string, string>? paramValues = null;
                    if (!string.IsNullOrEmpty(job.ParameterValuesJson))
                        paramValues = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            job.ParameterValuesJson);

                    var instanceId = await launcher.LaunchAsync(
                        job.TaskDefinitionId.Value,
                        paramValues,
                        job.CallerAgentId,
                        ct);

                    logger.LogInformation(
                        "Scheduled job {Name} ({Id}) launched task instance {InstanceId}.",
                        job.Name, job.Id, instanceId);
                }

                job.Status = ScheduledTaskStatus.Completed;
                job.RetryCount = 0;
                job.LastError = null;

                bool wasMissed = (now - job.NextRunAt) > missedThreshold;
                if (job.MissedFirePolicy == MissedFirePolicy.Skip && wasMissed)
                {
                    AdvanceNextRunAt(job, now);
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                AdvanceNextRunAt(job, now);

                logger.LogInformation("Scheduled job {Name} ({Id}) completed.", job.Name, job.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                job.RetryCount++;
                job.LastError = ex.Message;

                if (job.RetryCount < job.MaxRetries)
                {
                    job.Status = ScheduledTaskStatus.Pending;
                    job.NextRunAt = now.AddSeconds(30 * job.RetryCount);
                    logger.LogWarning(ex, "Scheduled job {Name} failed (attempt {Attempt}/{Max}), retrying.",
                        job.Name, job.RetryCount, job.MaxRetries);
                }
                else
                {
                    job.Status = ScheduledTaskStatus.Failed;
                    logger.LogError(ex, "Scheduled job {Name} failed permanently after {Max} attempts.",
                        job.Name, job.MaxRetries);
                }
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private static void AdvanceNextRunAt(ScheduledJobDB job, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(job.CronExpression))
        {
            var next = CronEvaluator.GetNextOccurrence(
                job.CronExpression, now, job.CronTimezone);

            job.Status    = next.HasValue ? ScheduledTaskStatus.Pending
                                          : ScheduledTaskStatus.Completed;
            job.NextRunAt = next ?? job.NextRunAt;
        }
        else if (job.RepeatInterval.HasValue)
        {
            job.Status    = ScheduledTaskStatus.Pending;
            job.NextRunAt = now.Add(job.RepeatInterval.Value);
        }
    }
}
