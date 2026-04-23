using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
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
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        var channel = Channel.CreateUnbounded<TaskOutputEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        var pauseGate = new PauseGate();

        var entry = new TaskRuntimeEntry(cts, channel, pauseGate);
        _entries[instanceId] = entry;

        return new TaskRuntimeInstance(instanceId, entry, this);
    }

    /// <summary>
    /// Remove the entry for a finished or failed instance and complete its
    /// output channel so any waiting SSE consumers see end-of-stream.
    /// </summary>
    public void Unregister(Guid instanceId)
    {
        if (_entries.TryRemove(instanceId, out var entry))
        {
            entry.OutputChannel.Writer.TryComplete();
            entry.Cts.Dispose();
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
        => _entries.TryGetValue(instanceId, out var e) ? e.OutputChannel.Reader : null;

    /// <summary>
    /// Cancel and stop a running instance.
    /// </summary>
    public async Task StopAsync(Guid instanceId, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(instanceId, out var entry))
        {
            entry.PauseGate.Resume(); // unblock if paused so cancel propagates
            await entry.Cts.CancelAsync();
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

        entry.PauseGate.Pause();
        await svc.AppendLogAsync(instanceId, "Task paused.", ct: ct);
        await WriteEventAsync(instanceId, TaskOutputEventType.StatusChange, "Paused");
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

        entry.PauseGate.Resume();
        await svc.AppendLogAsync(instanceId, "Task resumed.", ct: ct);
        await WriteEventAsync(instanceId, TaskOutputEventType.StatusChange, "Running");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal helpers (used via TaskRuntimeInstance)
    // ═══════════════════════════════════════════════════════════════

    internal long IncrementSequence(Guid instanceId)
        => _entries.TryGetValue(instanceId, out var e)
            ? Interlocked.Increment(ref e.SequenceCounter)
            : 1;

    internal Task WaitIfPausedAsync(Guid instanceId, CancellationToken ct)
        => _entries.TryGetValue(instanceId, out var e)
            ? e.PauseGate.WaitIfPausedAsync(ct)
            : Task.CompletedTask;

    internal async Task WriteEventAsync(Guid instanceId, TaskOutputEventType type, string? data)
    {
        if (!_entries.TryGetValue(instanceId, out var entry))
            return;

        var seq = IncrementSequence(instanceId);
        var evt = new TaskOutputEvent(type, seq, DateTimeOffset.UtcNow, data);
        await entry.OutputChannel.Writer.WriteAsync(evt);
    }

    // ═══════════════════════════════════════════════════════════════
    // Startup recovery
    // ═══════════════════════════════════════════════════════════════

    private async Task RecoverStaleInstancesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<TaskService>();

        // Find instances that were left in Running or Paused from a previous
        // process lifetime.  We cannot safely replay arbitrary side effects,
        // so the conservative policy is to mark them as Failed with a recovery
        // note.  A future phase may identify listener-style or idempotent tasks
        // and attempt rehydration.
        var stale = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(
                db.TaskInstances.Where(i =>
                    i.Status == TaskInstanceStatus.Running ||
                    i.Status == TaskInstanceStatus.Paused),
                ct);

        if (stale.Count == 0)
            return;

        logger.LogWarning(
            "TaskRuntimeHost: found {Count} stale instance(s) from previous session. " +
            "Marking as Failed (restart recovery).", stale.Count);

        foreach (var instance in stale)
        {
            var previous = instance.Status;
            instance.Status = TaskInstanceStatus.Failed;
            instance.ErrorMessage =
                $"Instance was {previous} when the application restarted. " +
                "Manual restart required.";
            instance.CompletedAt ??= DateTimeOffset.UtcNow;

            await svc.AppendLogAsync(
                instance.Id,
                $"Recovery: instance was {previous} at startup — marked Failed.",
                "Recovery",
                ct);

            logger.LogInformation(
                "TaskRuntimeHost: instance {InstanceId} ({Previous}) marked Failed (recovery).",
                instance.Id, previous);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task CancelEntryAsync(Guid instanceId)
    {
        if (!_entries.TryGetValue(instanceId, out var entry))
            return;
        try
        {
            entry.PauseGate.Resume();
            await entry.Cts.CancelAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error cancelling entry {InstanceId} during shutdown.", instanceId);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Inner types
    // ═══════════════════════════════════════════════════════════════

    /// <summary>All host-owned state for one active task instance.</summary>
    internal sealed class TaskRuntimeEntry(
        CancellationTokenSource cts,
        Channel<TaskOutputEvent> outputChannel,
        PauseGate pauseGate)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public Channel<TaskOutputEvent> OutputChannel { get; } = outputChannel;
        public PauseGate PauseGate { get; } = pauseGate;
        public long SequenceCounter;
    }

    /// <summary>Cooperative async pause gate.</summary>
    internal sealed class PauseGate
    {
        private volatile TaskCompletionSource _signal = Signaled();

        public void Pause()
        {
            if (_signal.Task.IsCompleted)
                Interlocked.Exchange(ref _signal, Paused());
        }

        public void Resume()
        {
            var next = Signaled();
            Interlocked.Exchange(ref _signal, next).TrySetResult();
        }

        public Task WaitIfPausedAsync(CancellationToken ct)
            => _signal.Task.WaitAsync(ct);

        private static TaskCompletionSource Paused()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource Signaled()
        {
            var s = Paused();
            s.TrySetResult();
            return s;
        }
    }
}

/// <summary>
/// A lightweight handle passed to <see cref="TaskOrchestrator"/> for one
/// running instance.  Delegates all operations to <see cref="TaskRuntimeHost"/>.
/// </summary>
public sealed class TaskRuntimeInstance
{
    private readonly TaskRuntimeHost.TaskRuntimeEntry _entry;
    private readonly TaskRuntimeHost _host;
    private readonly Guid _instanceId;

    internal TaskRuntimeInstance(
        Guid instanceId,
        TaskRuntimeHost.TaskRuntimeEntry entry,
        TaskRuntimeHost host)
    {
        _instanceId = instanceId;
        _entry = entry;
        _host = host;
    }
    /// <summary>The cancellation token for this instance's execution.</summary>
    public CancellationToken CancellationToken => _entry.Cts.Token;

    /// <summary>Write a structured event to the instance's output channel.</summary>
    public Task WriteEventAsync(TaskOutputEventType type, string? data)
        => _host.WriteEventAsync(_instanceId, type, data);

    /// <summary>
    /// Wait if the instance has been cooperatively paused.
    /// Returns immediately when the instance is running.
    /// </summary>
    public Task WaitIfPausedAsync(CancellationToken ct)
        => _host.WaitIfPausedAsync(_instanceId, ct);

    /// <summary>Increment and return the next output sequence number.</summary>
    public long IncrementSequence() => _host.IncrementSequence(_instanceId);
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
