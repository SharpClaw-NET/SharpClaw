using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Tasks;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Compilation;
using SharpClaw.Core.Tasks.Runtime;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

/// <summary>
/// Application host adapter for compiled task execution. Startup compilation,
/// EF status persistence, runtime registration, and output streaming stay in
/// the application; plan interpretation lives in SharpClaw.Core.
/// </summary>
public sealed class TaskOrchestrator(
    IServiceScopeFactory scopeFactory,
    TaskRuntimeHost runtimeHost,
    TaskStartupPreparationEngine startupPreparation,
    ILogger<TaskOrchestrator> logger)
{
    private readonly TaskAdministrationEngine _tasks = new();
    private readonly TaskRuntimeLifecycleEngine _runtimeLifecycle = new();

    /// <summary>
    /// Compile and start executing a queued task instance.
    /// </summary>
    public async Task StartAsync(Guid instanceId, CancellationToken ct = default)
    {
        var startupTiming = Stopwatch.StartNew();
        using var startupScope = scopeFactory.CreateScope();
        var startupDb = startupScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var startupTaskService = startupScope.ServiceProvider.GetRequiredService<TaskService>();
        var startupResolver = startupScope.ServiceProvider.GetRequiredService<IPersistenceEntityResolver>();

        var instance = await startupResolver.FindAsync<TaskInstanceDB>(
                startupDb,
                instanceId,
                ct)
            ?? throw new InvalidOperationException(
                $"Task instance {instanceId} not found.");

        if (instance.TaskDefinition is null)
        {
            instance.TaskDefinition = await startupResolver.FindAsync<TaskDefinitionDB>(
                    startupDb,
                    instance.TaskDefinitionId,
                    ct)
                ?? throw new InvalidOperationException(
                    $"Task definition {instance.TaskDefinitionId} for instance {instanceId} not found.");
        }

        var startup = startupPreparation.Prepare(
            instance,
            instance.TaskDefinition);

        if (startup.Kind == TaskStartupPreparationKind.CompilationFailed)
        {
            var errors = startup.CompilationErrors
                ?? throw new InvalidOperationException(
                    "Core task startup preparation returned compilation failure without diagnostics.");
            _tasks.ApplyCompilationFailure(instance, errors);
            await startupDb.SaveChangesAsync(ct);
            logger.LogDebug(
                "Task instance {InstanceId} compilation failed after {ElapsedMs}ms with {DiagnosticCount} diagnostic(s).",
                instanceId,
                startupTiming.ElapsedMilliseconds,
                startup.DiagnosticCount);
            return;
        }

        var plan = startup.Plan
            ?? throw new InvalidOperationException(
                "Core task startup preparation did not return a compiled plan.");
        var runtime = runtimeHost.Register(instanceId, ct);

        if (!await startupTaskService.TryMarkInstanceRunningAsync(instanceId, ct))
        {
            runtimeHost.Unregister(instanceId);
            throw new InvalidOperationException(
                $"Task instance {instanceId} could not transition to Running.");
        }

        await EmitRuntimeEventPlanAsync(
            instanceId,
            _runtimeLifecycle.BuildStartedPlan(),
            startupTaskService,
            runtime,
            ct);

        startupTiming.Stop();
        logger.LogDebug(
            "Task instance {InstanceId} compiled and entered Running in {ElapsedMs}ms. TaskName={TaskName} StepCount={StepCount}",
            instanceId,
            startupTiming.ElapsedMilliseconds,
            PathGuard.SanitizeForLog(plan.TaskName),
            plan.ExecutionSteps.Count);

        _ = Task.Run(
            () => ExecutePlanAsync(
                instanceId,
                plan,
                runtime),
            CancellationToken.None);
    }

    /// <summary>
    /// Request cancellation of a running instance.
    /// </summary>
    public Task StopAsync(Guid instanceId, CancellationToken ct = default)
        => runtimeHost.StopAsync(instanceId, ct);

    /// <summary>
    /// Cooperatively pause a running task instance.
    /// </summary>
    public Task<bool> PauseAsync(Guid instanceId, CancellationToken ct = default)
        => runtimeHost.PauseAsync(instanceId, ct);

    /// <summary>
    /// Resume a paused task instance.
    /// </summary>
    public Task<bool> ResumeAsync(Guid instanceId, CancellationToken ct = default)
        => runtimeHost.ResumeAsync(instanceId, ct);

    /// <summary>
    /// Get a channel reader for streaming task output events.
    /// </summary>
    public ChannelReader<TaskOutputEvent>? GetOutputReader(Guid instanceId)
        => runtimeHost.GetOutputReader(instanceId);

    private async Task ExecutePlanAsync(
        Guid instanceId,
        CompiledTaskPlan plan,
        TaskRuntimeInstance runtime)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var taskService = scope.ServiceProvider.GetRequiredService<TaskService>();
            var executionEngine = scope.ServiceProvider
                .GetRequiredService<TaskPlanExecutionEngine>();
            var host = new EfTaskPlanExecutionHost(db, taskService, _tasks);

            var outcome = await executionEngine.ExecuteAsync(
                new TaskPlanExecutionRequest(
                    instanceId,
                    plan,
                    runtime,
                    scope.ServiceProvider,
                    host,
                    runtime.CancellationToken));

            logger.LogDebug(
                "Task instance {InstanceId} execution engine finished with {Status} in {ElapsedMs}ms.",
                instanceId,
                outcome.Status,
                outcome.Elapsed.TotalMilliseconds);
        }
        finally
        {
            runtimeHost.Unregister(instanceId);
        }
    }

    private static async Task EmitRuntimeEventPlanAsync(
        Guid instanceId,
        TaskRuntimeEventPlan plan,
        TaskService taskService,
        TaskRuntimeInstance runtime,
        CancellationToken ct = default)
    {
        if (plan.LogMessage is not null)
            await taskService.AppendLogAsync(
                instanceId,
                plan.LogMessage,
                plan.LogLevel,
                ct);

        foreach (var evt in plan.OutputEvents)
            await runtime.WriteEventAsync(evt.Type, evt.Data, ct);
    }

    private sealed class EfTaskPlanExecutionHost(
        SharpClawDbContext db,
        TaskService taskService,
        TaskAdministrationEngine tasks) : ITaskPlanExecutionHost
    {
        public async Task<Guid?> LoadInitialChannelIdAsync(
            Guid instanceId,
            CancellationToken ct)
        {
            return await db.TaskInstances
                .Where(i => i.Id == instanceId)
                .Select(i => i.ChannelId)
                .FirstOrDefaultAsync(ct);
        }

        public async Task PersistOutputAsync(
            Guid instanceId,
            long sequence,
            string? outputJson,
            CancellationToken ct)
        {
            var instance = await db.TaskInstances.FindAsync([instanceId], ct);
            if (instance is null)
                return;

            db.TaskOutputEntries.Add(tasks.ApplyOutput(
                instance,
                sequence,
                outputJson));
            await db.SaveChangesAsync(ct);
        }

        public async Task PersistSharedDataSnapshotAsync(
            Guid instanceId,
            string? lightSnapshot,
            string? bigSnapshotJson,
            CancellationToken ct)
        {
            var instance = await db.TaskInstances.FindAsync([instanceId], ct);
            if (instance is null)
                return;

            instance.LightDataSnapshot = lightSnapshot;
            instance.BigDataSnapshotJson = bigSnapshotJson;
            await db.SaveChangesAsync(ct);
        }

        public Task AppendLogAsync(
            Guid instanceId,
            string message,
            string level,
            CancellationToken ct) =>
            taskService.AppendLogAsync(instanceId, message, level, ct);

        public async Task MarkTerminalStatusAsync(
            Guid instanceId,
            TaskInstanceStatus status,
            CancellationToken ct)
        {
            var instance = await db.TaskInstances.FindAsync([instanceId], ct);
            if (instance is null)
                return;

            tasks.ApplyTerminalStatus(instance, status);
            await db.SaveChangesAsync(ct);
        }

        public async Task MarkFailedAsync(
            Guid instanceId,
            string error,
            CancellationToken ct)
        {
            var instance = await db.TaskInstances.FindAsync([instanceId], ct);
            if (instance is null)
                return;

            tasks.ApplyFailure(instance, error);
            await db.SaveChangesAsync(ct);
        }
    }
}
