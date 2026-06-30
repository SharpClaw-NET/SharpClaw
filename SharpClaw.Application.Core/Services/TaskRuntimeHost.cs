using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Runtime;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Supervises all running task instances for the lifetime of the application.
/// Owns per-instance cancellation sources, pause gates, output channels, and
/// sequence counters.  <see cref="TaskOrchestrator"/> is the execution engine;
/// this host is the long-lived registry and recovery manager.
/// </summary>
public sealed class TaskRuntimeHost(
    IServiceScopeFactory scopeFactory,
    ILogger<TaskRuntimeHost> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, TaskRuntimeEntry> _entries = new();
    private readonly TaskCompletionSource _recoveryComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskAdministrationEngine _tasks = new();
    private readonly TaskRuntimeLifecycleEngine _runtimeLifecycle = new();

    /// <summary>
    /// Completes when startup recovery has finished.  Awaitable in tests and
    /// any code that must not run before stale instances are resolved.
    /// </summary>
    public Task RecoveryComplete => _recoveryComplete.Task;

    // ═══════════════════════════════════════════════════════════════
    // IHostedService lifecycle
    // ═══════════════════════════════════════════════════════════════

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverStaleInstancesAsync(stoppingToken);
        _recoveryComplete.TrySetResult();
        // Host stays alive until shutdown; active entries are managed independently.
        await stoppingToken.WhenCancelledAsync();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("TaskRuntimeHost stopping — cancelling {Count} active instance(s).", _entries.Count);

        var cancellations = _entries.Keys.ToList();
        foreach (var id in cancellations)
            await CancelEntryAsync(id);

        await base.StopAsync(cancellationToken);
    }

    // ═══════════════════════════════════════════════════════════════
    // Registration (called by TaskOrchestrator when execution begins)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register a new runtime entry for an instance that is about to start.
    /// Returns a <see cref="TaskRuntimeInstance"/> handle the orchestrator uses
    /// to interact with the host-owned state during execution.
    /// </summary>
    public TaskRuntimeInstance Register(Guid instanceId, CancellationToken linkedToken)
    {
        var entry = TaskRuntimeEntry.Create(linkedToken);
        _entries[instanceId] = entry;

        return entry.CreateInstance(instanceId);
    }

    /// <summary>
    /// Remove the entry for a finished or failed instance and complete its
    /// output channel so any waiting SSE consumers see end-of-stream.
    /// </summary>
    public void Unregister(Guid instanceId)
    {
        if (_entries.TryRemove(instanceId, out var entry))
        {
            entry.CompleteOutput();
            entry.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Operational API (used by handlers and services)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Returns true if an entry exists for the instance.</summary>
    public bool IsRunning(Guid instanceId) => _entries.ContainsKey(instanceId);

    /// <summary>
    /// Get a <see cref="ChannelReader{T}"/> for streaming instance output.
    /// Returns <c>null</c> when no active entry exists.
    /// </summary>
    public ChannelReader<TaskOutputEvent>? GetOutputReader(Guid instanceId)
        => _entries.TryGetValue(instanceId, out var e) ? e.OutputReader : null;

    /// <summary>
    /// Cancel and stop a running instance.
    /// </summary>
    public async Task StopAsync(Guid instanceId, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(instanceId, out var entry))
        {
            entry.Resume(); // unblock if paused so cancel propagates
            await entry.CancelAsync();
        }

        // Persist the stop through the service (status → Cancelled)
        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TaskService>();
        await svc.StopInstanceAsync(instanceId, ct);
    }

    /// <summary>
    /// Cooperatively pause a running instance.  Returns false if no active
    /// entry exists or the instance is not running.
    /// </summary>
    public async Task<bool> PauseAsync(Guid instanceId, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(instanceId, out var entry))
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
    /// Resume a paused instance.  Returns false if no active entry exists or
    /// the instance is not paused.
    /// </summary>
    public async Task<bool> ResumeAsync(Guid instanceId, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(instanceId, out var entry))
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

    // ═══════════════════════════════════════════════════════════════
    // Startup recovery
    // ═══════════════════════════════════════════════════════════════

    private async Task RecoverStaleInstancesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<TaskService>();
        var entities = scope.ServiceProvider.GetRequiredService<IPersistenceEntityResolver>();

        // Find instances that were left in Running or Paused from a previous
        // process lifetime.  We cannot safely replay arbitrary side effects,
        // so the conservative policy is to mark them as Failed with a recovery
        // note.  A future phase may identify listener-style or idempotent tasks
        // and attempt rehydration.
        var stale = await entities.QueryAsync<TaskInstanceDB>(
            db,
            i => i.Status == TaskInstanceStatus.Running || i.Status == TaskInstanceStatus.Paused,
            hint: null,
            ct);

        if (stale.Count == 0)
            return;

        logger.LogWarning(
            "TaskRuntimeHost: found {Count} stale instance(s) from previous session. " +
            "Marking as Failed (restart recovery).", stale.Count);

        foreach (var instance in stale)
        {
            var recovery = _tasks.ApplyRestartRecovery(instance);

            await svc.AppendLogAsync(
                instance.Id,
                recovery.LogMessage,
                "Recovery",
                ct);

            logger.LogInformation(
                "TaskRuntimeHost: instance {InstanceId} ({Previous}) marked Failed (recovery).",
                instance.Id, recovery.PreviousStatus);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task CancelEntryAsync(Guid instanceId)
    {
        if (!_entries.TryGetValue(instanceId, out var entry))
            return;
        try
        {
            entry.Resume();
            await entry.CancelAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error cancelling entry {InstanceId} during shutdown.", instanceId);
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
            await taskService.AppendLogAsync(instanceId, plan.LogMessage, plan.LogLevel, ct);

        foreach (var evt in plan.OutputEvents)
            await entry.WriteEventAsync(evt.Type, evt.Data, ct);
    }

}

internal static class CancellationTokenExtensions
{
    /// <summary>Returns a task that completes when the token is cancelled.</summary>
    public static Task WhenCancelledAsync(this CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), tcs);
        return tcs.Task;
    }
}
