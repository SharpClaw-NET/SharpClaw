using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ScheduledTaskService(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledTaskService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ScheduledTaskService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueTasksAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in task processing loop.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProcessDueTasksAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        var now = DateTimeOffset.UtcNow;

        var dueTasks = await db.ScheduledTasks
            .Where(t => t.Status == ScheduledTaskStatus.Pending && t.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (var task in dueTasks)
        {
            task.Status = ScheduledTaskStatus.Running;
            task.LastRunAt = now;
            await db.SaveChangesAsync(ct);

            try
            {
                logger.LogInformation("Executing task {TaskName} ({TaskId}).", task.Name, task.Id);

                // TODO: dispatch actual task work here
                await Task.CompletedTask;

                task.Status = ScheduledTaskStatus.Completed;
                task.RetryCount = 0;
                task.LastError = null;

                if (task.RepeatInterval.HasValue)
                {
                    task.Status = ScheduledTaskStatus.Pending;
                    task.NextRunAt = now.Add(task.RepeatInterval.Value);
                }

                logger.LogInformation("Task {TaskName} ({TaskId}) completed.", task.Name, task.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                task.RetryCount++;
                task.LastError = ex.Message;

                if (task.RetryCount < task.MaxRetries)
                {
                    task.Status = ScheduledTaskStatus.Pending;
                    task.NextRunAt = now.AddSeconds(30 * task.RetryCount); // linear backoff
                    logger.LogWarning(ex, "Task {TaskName} failed (attempt {Attempt}/{Max}), retrying.",
                        task.Name, task.RetryCount, task.MaxRetries);
                }
                else
                {
                    task.Status = ScheduledTaskStatus.Failed;
                    logger.LogError(ex, "Task {TaskName} failed permanently after {Max} attempts.",
                        task.Name, task.MaxRetries);
                }
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
