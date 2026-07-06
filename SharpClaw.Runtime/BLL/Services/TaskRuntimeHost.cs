using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Runtime;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

/// <summary>
/// Supervises running task instances for the lifetime of the application.
/// Core owns the active-entry registry; this host owns hosted-service
/// lifetime, EF recovery, and persistence-side status transitions.
/// </summary>
public sealed class TaskRuntimeHost(
    IServiceScopeFactory scopeFactory,
    TaskRuntimeRegistry runtimeRegistry,
    ILogger<TaskRuntimeHost> logger) : BackgroundService
{
    private readonly TaskCompletionSource _recoveryComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskAdministrationEngine _tasks = new();
    private readonly TaskRuntimeLifecycleEngine _runtimeLifecycle = new();

    /// <summary>
    /// Completes when startup recovery has finished.
    /// </summary>
    public Task RecoveryComplete => _recoveryComplete.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverStaleInstancesAsync(stoppingToken);
        _recoveryComplete.TrySetResult();
        await stoppingToken.WhenCancelledAsync();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "TaskRuntimeHost stopping; cancelling {Count} active instance(s).",
            runtimeRegistry.ActiveCount);

        foreach (var id in runtimeRegistry.ActiveInstanceIds)
            await CancelEntryAsync(id);

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Registers a new runtime entry for an instance that is about to start.
    /// </summary>
    public TaskRuntimeInstance Register(
        Guid instanceId,
        CancellationToken linkedToken)
    {
        return runtimeRegistry.Register(instanceId, linkedToken);
    }

    /// <summary>
    /// Removes a finished or failed instance and completes its output stream.
    /// </summary>
    public void Unregister(Guid instanceId)
    {
        runtimeRegistry.Unregister(instanceId);
    }

    /// <summary>
    /// Returns true if an entry exists for the instance.
    /// </summary>
    public bool IsRunning(Guid instanceId)
    {
        return runtimeRegistry.IsRunning(instanceId);
    }

    /// <summary>
    /// Gets a streaming output reader for an active instance.
    /// </summary>
    public ChannelReader<TaskOutputEvent>? GetOutputReader(Guid instanceId)
    {
        return runtimeRegistry.GetOutputReader(instanceId);
    }

    /// <summary>
    /// Cancels a running instance and persists the host stop transition.
    /// </summary>
    public async Task StopAsync(Guid instanceId, CancellationToken ct = default)
    {
        await runtimeRegistry.CancelAsync(instanceId);

        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TaskService>();
        await svc.StopInstanceAsync(instanceId, ct);
    }

    /// <summary>
    /// Cooperatively pauses a running instance.
    /// </summary>
    public async Task<bool> PauseAsync(
        Guid instanceId,
        CancellationToken ct = default)
    {
        if (!runtimeRegistry.TryGetEntry(instanceId, out var entry))
            return false;

        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TaskService>();

        if (!await svc.PauseInstanceAsync(instanceId, ct))
            return false;

        entry.Pause();
        await EmitRuntimeEventPlanAsync(
            instanceId,
            _runtimeLifecycle.BuildPausedPlan(),
            svc,
            entry,
            ct);
        return true;
    }

    /// <summary>
    /// Resumes a paused instance.
    /// </summary>
    public async Task<bool> ResumeAsync(
        Guid instanceId,
        CancellationToken ct = default)
    {
        if (!runtimeRegistry.TryGetEntry(instanceId, out var entry))
            return false;

        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TaskService>();

        if (!await svc.ResumeInstanceAsync(instanceId, ct))
            return false;

        entry.Resume();
        await EmitRuntimeEventPlanAsync(
            instanceId,
            _runtimeLifecycle.BuildResumedPlan(),
            svc,
            entry,
            ct);
        return true;
    }

    private async Task RecoverStaleInstancesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<TaskService>();
        var entities = scope.ServiceProvider
            .GetRequiredService<IPersistenceEntityResolver>();

        var stale = await entities.QueryAsync<TaskInstanceDB>(
            db,
            i => i.Status == TaskInstanceStatus.Running
                || i.Status == TaskInstanceStatus.Paused,
            hint: null,
            ct);

        if (stale.Count == 0)
            return;

        logger.LogWarning(
            "TaskRuntimeHost found {Count} stale instance(s) from previous " +
            "session. Marking them Failed for restart recovery.",
            stale.Count);

        foreach (var instance in stale)
        {
            var recovery = _tasks.ApplyRestartRecovery(instance);

            await svc.AppendLogAsync(
                instance.Id,
                recovery.LogMessage,
                "Recovery",
                ct);

            logger.LogInformation(
                "TaskRuntimeHost marked instance {InstanceId} ({Previous}) " +
                "Failed during restart recovery.",
                instance.Id,
                recovery.PreviousStatus);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task CancelEntryAsync(Guid instanceId)
    {
        try
        {
            await runtimeRegistry.CancelAsync(instanceId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Error cancelling entry {InstanceId} during shutdown.",
                instanceId);
        }
    }

    private static async Task EmitRuntimeEventPlanAsync(
        Guid instanceId,
        TaskRuntimeEventPlan plan,
        TaskService taskService,
        TaskRuntimeEntry entry,
        CancellationToken ct)
    {
        if (plan.LogMessage is not null)
        {
            await taskService.AppendLogAsync(
                instanceId,
                plan.LogMessage,
                plan.LogLevel,
                ct);
        }

        foreach (var evt in plan.OutputEvents)
            await entry.WriteEventAsync(evt.Type, evt.Data, ct);
    }
}

internal static class CancellationTokenExtensions
{
    /// <summary>
    /// Returns a task that completes when the token is cancelled.
    /// </summary>
    public static Task WhenCancelledAsync(this CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            tcs);
        return tcs.Task;
    }
}
