using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Core.Tasks;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Compilation;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Runtime;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

/// <summary>
/// Executes a <see cref="CompiledTaskPlan"/> by interpreting its
/// <see cref="TaskStepDefinition"/> tree.  Each running instance gets
/// its own runtime entry managed by <see cref="TaskRuntimeHost"/>.
/// <para>
/// The orchestrator does <b>not</b> manage persistence — it calls back
/// into <see cref="TaskService"/> for DB operations and delegates agent
/// interaction to <see cref="ChatService"/>.  Runtime state (cancellation,
/// pause gates, output channels) is owned by <see cref="TaskRuntimeHost"/>.
/// </para>
/// </summary>
public sealed class TaskOrchestrator(
    SharpClawDbContext db,
    IPersistenceEntityResolver entities,
    TaskService taskService,
    IServiceScopeFactory scopeFactory,
    TaskRuntimeHost runtimeHost,
    IEnumerable<ITaskStepExecutorExtension> stepExtensions,
    ILogger<TaskOrchestrator> logger)
{
    private readonly IReadOnlyList<ITaskStepExecutorExtension> _stepExtensions = [.. stepExtensions];
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TaskAdministrationEngine _tasks = new();
    private readonly TaskExpressionEngine _expressions = new();
    private readonly TaskRuntimeLifecycleEngine _runtimeLifecycle = new();

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Compile and start executing a task instance.
    /// </summary>
    public async Task StartAsync(Guid instanceId, CancellationToken ct = default)
    {
        var startupTiming = Stopwatch.StartNew();
        using var startupScope = scopeFactory.CreateScope();
        var startupDb = startupScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var startupTaskService = startupScope.ServiceProvider.GetRequiredService<TaskService>();
        var startupResolver = startupScope.ServiceProvider.GetRequiredService<IPersistenceEntityResolver>();

        var instance = await startupResolver.FindAsync<TaskInstanceDB>(startupDb, instanceId, ct)
            ?? throw new InvalidOperationException($"Task instance {instanceId} not found.");

        if (instance.TaskDefinition is null)
        {
            instance.TaskDefinition = await startupResolver.FindAsync<TaskDefinitionDB>(startupDb, instance.TaskDefinitionId, ct)
                ?? throw new InvalidOperationException(
                    $"Task definition {instance.TaskDefinitionId} for instance {instanceId} not found.");
        }

        if (instance.Status != TaskInstanceStatus.Queued)
            throw new InvalidOperationException(
                $"Task instance {instanceId} is {instance.Status}, expected Queued.");

        // Parse parameter values from the stored JSON
        Dictionary<string, object?>? paramValues = null;
        if (instance.ParameterValuesJson is not null)
        {
            paramValues = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                instance.ParameterValuesJson);
        }

        // Compile the task plan
        var compilationResult = TaskScriptEngine.ProcessScript(
            instance.TaskDefinition.SourceText, paramValues);

        if (compilationResult.Plan is null)
        {
            var errors = string.Join("; ", compilationResult.Diagnostics.Select(d => d.Message));
            _tasks.ApplyCompilationFailure(instance, errors);
            await startupDb.SaveChangesAsync(ct);
            logger.LogDebug(
                "Task instance {InstanceId} compilation failed after {ElapsedMs}ms with {DiagnosticCount} diagnostic(s).",
                instanceId, startupTiming.ElapsedMilliseconds,
                compilationResult.Diagnostics.Count);
            return;
        }

        // Prepare runtime entry in the host
        var runtime = runtimeHost.Register(instanceId, ct);

        if (!await startupTaskService.TryMarkInstanceRunningAsync(instanceId, ct))
        {
            runtimeHost.Unregister(instanceId);
            throw new InvalidOperationException($"Task instance {instanceId} could not transition to Running.");
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
            instanceId, startupTiming.ElapsedMilliseconds,
            PathGuard.SanitizeForLog(compilationResult.Plan.TaskName),
            compilationResult.Plan.ExecutionSteps.Count);

        // Execute in background so the caller returns immediately
        _ = Task.Run(() => ExecutePlanAsync(instanceId, compilationResult.Plan, runtime, runtime.CancellationToken), CancellationToken.None);
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
    /// Returns <c>null</c> if no instance is running with the given ID.
    /// </summary>
    public ChannelReader<TaskOutputEvent>? GetOutputReader(Guid instanceId)
        => runtimeHost.GetOutputReader(instanceId);

    // ═══════════════════════════════════════════════════════════════
    // Execution engine
    // ═══════════════════════════════════════════════════════════════

    private async Task ExecutePlanAsync(
        Guid instanceId,
        CompiledTaskPlan plan,
        TaskRuntimeInstance runtime,
        CancellationToken ct)
    {
        // Create a dedicated DI scope for the background execution.
        // The HTTP request scope that called StartAsync is already disposed
        // by the time this Task.Run body executes, so the constructor-injected
        // scoped services (db, taskService, chatService, agentJobService) are
        // no longer usable.  Resolving fresh instances from a new scope avoids
        // the ObjectDisposedException.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var taskService = scope.ServiceProvider.GetRequiredService<TaskService>();
        var chatService = scope.ServiceProvider.GetService<ChatService>();
        var agentJobService = scope.ServiceProvider.GetService<AgentJobService>();

        var context = new TaskExecutionContext(instanceId, plan, runtime, ct)
        {
            Services = scope.ServiceProvider,
        };

        // ── Set up shared data store for inter-agent communication ──
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.TaskName = plan.TaskName;
        store.TaskDescription = plan.Description;
        store.TaskSourceText = plan.Definition.SourceText;
        store.TaskParametersJson = plan.ParameterValues.Count > 0
            ? JsonSerializer.Serialize(plan.ParameterValues)
            : null;
        store.AllowedOutputFormat = plan.AgentOutputFormat;
        store.RegisterBuiltInTools();
        store.OnAgentOutput = async data => await EmitOutputAsync(instanceId, data, db, runtime);
        store.OnSharedDataChanged = async (description, lightSnapshot, bigSnapshotJson) =>
        {
            var instance = await db.TaskInstances.FindAsync(instanceId);
            if (instance is not null)
            {
                instance.LightDataSnapshot = lightSnapshot;
                instance.BigDataSnapshotJson = bigSnapshotJson;
                await db.SaveChangesAsync();
            }
            await EmitRuntimeEventPlanAsync(
                instanceId,
                _runtimeLifecycle.BuildSharedDataChangedPlan(description),
                taskService,
                runtime);
        };

        // Register custom [ToolCall] hook callbacks
        foreach (var hook in plan.ToolCallHooks)
        {
            store.RegisterCustomToolHook(hook, async (args, hookCt) =>
            {
                // Set hook parameters as variables for body execution
                foreach (var param in hook.Parameters)
                {
                    var val = args?.TryGetProperty(param.Name, out var jp) == true
                        ? jp.ValueKind == JsonValueKind.String ? jp.GetString() : jp.GetRawText()
                        : null;
                    context.Variables[param.Name] = val;
                }

                // Execute the hook body steps
                foreach (var step in hook.Body)
                {
                    hookCt.ThrowIfCancellationRequested();
                    var executionResult = await ExecuteStepAsync(step, context, db, taskService, chatService, agentJobService);
                    if (executionResult == TaskStepExecutionResult.Return)
                    {
                        break;
                    }
                }

                if (hook.ReturnVariable is not null && context.Variables.TryGetValue(hook.ReturnVariable, out var result))
                    return result?.ToString() ?? string.Empty;

                return string.Empty;
            });
        }

        var executionTiming = Stopwatch.StartNew();
        try
        {
            // Resolve channel — may be null when the task was started with a
            // context instead of a direct channel.  In that case the task is
            // expected to call CreateChannel early; channel-dependent steps
            // (Chat, CreateThread, etc.) will throw if invoked before then.
            var initialChannel = await db.TaskInstances
                .Where(i => i.Id == instanceId)
                .Select(i => i.ChannelId)
                .FirstOrDefaultAsync(ct);
            context.ChannelId = initialChannel ?? Guid.Empty;

            foreach (var step in plan.ExecutionSteps)
            {
                ct.ThrowIfCancellationRequested();
                await runtime.WaitIfPausedAsync(ct);
                var executionResult = await ExecuteStepAsync(step, context, db, taskService, chatService, agentJobService);
                if (executionResult == TaskStepExecutionResult.Return)
                {
                    break;
                }
            }

            await CompleteInstanceAsync(instanceId, TaskInstanceStatus.Completed, db, taskService, runtime);
            executionTiming.Stop();
            logger.LogDebug(
                "Task instance {InstanceId} completed in {ElapsedMs}ms.",
                instanceId, executionTiming.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            await CompleteInstanceAsync(instanceId, TaskInstanceStatus.Cancelled, db, taskService, runtime);
            executionTiming.Stop();
            logger.LogDebug(
                "Task instance {InstanceId} cancelled after {ElapsedMs}ms.",
                instanceId, executionTiming.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            await FailInstanceAsync(instanceId, ex.Message, db, taskService, runtime);
            executionTiming.Stop();
            logger.LogWarning(
                ex,
                "Task instance {InstanceId} failed after {ElapsedMs}ms.",
                instanceId, executionTiming.ElapsedMilliseconds);
        }
        finally
        {
            TaskSharedData.Remove(instanceId);
            runtimeHost.Unregister(instanceId);
        }
    }

    private async Task<TaskStepExecutionResult> ExecuteStepAsync(
        TaskStepDefinition step, TaskExecutionContext context,
        SharpClawDbContext db, TaskService taskService,
        ChatService? chatService, AgentJobService? agentJobService)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        await WaitIfPausedAsync(context);

        var stepKey = step.StepKey ?? "";
        var executor = _stepExtensions.FirstOrDefault(e => e.CanExecute(stepKey));
        if (executor is null)
            return TaskStepExecutionResult.Continue;

        var moduleCtx = new TaskStepContextAdapter(context, this, taskService);
        var stepTiming = Stopwatch.StartNew();

        // Invocation-aware path: raw step access for control-flow primitives.
        if (executor is ITaskStepInvocationExecutor invocationExecutor)
        {
            var result = await invocationExecutor.ExecuteInvocationAsync(step, moduleCtx);
            stepTiming.Stop();
            logger.LogDebug(
                "Task instance {InstanceId} step {StepKey} completed in {ElapsedMs}ms. Result={Result}",
                context.InstanceId, PathGuard.SanitizeForLog(stepKey),
                stepTiming.ElapsedMilliseconds, result);
            return result == TaskStepResult.Return
                ? TaskStepExecutionResult.Return
                : TaskStepExecutionResult.Continue;
        }

        // Resolved-argument path: traditional module steps that consume runtime values.
        var resolvedArgs = step.Arguments?.Select(a => _expressions.ResolveExpression(a, context.Variables)).ToList();
        if (step.TypeName is not null)
        {
            resolvedArgs ??= [];
            resolvedArgs.Insert(0, step.TypeName);
        }
        var resolvedExpr = step.Expression is not null
            ? _expressions.ResolveExpression(step.Expression, context.Variables)
            : null;
        var keepGoing = await executor.ExecuteAsync(stepKey, moduleCtx, resolvedArgs, resolvedExpr, step.ResultVariable);
        stepTiming.Stop();
        logger.LogDebug(
            "Task instance {InstanceId} step {StepKey} completed in {ElapsedMs}ms. Continue={Continue}",
            context.InstanceId, PathGuard.SanitizeForLog(stepKey),
            stepTiming.ElapsedMilliseconds, keepGoing);
        return keepGoing ? TaskStepExecutionResult.Continue : TaskStepExecutionResult.Return;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static Task WaitIfPausedAsync(TaskExecutionContext context)
        => context.Runtime.WaitIfPausedAsync(context.CancellationToken);

    private async Task EmitOutputAsync(Guid instanceId, string? outputJson, SharpClawDbContext db, TaskRuntimeInstance runtime)
    {
        // Persist the latest output snapshot
        var instance = await db.TaskInstances.FindAsync(instanceId);
        if (instance is not null)
        {
            var seq = runtime.IncrementSequence();
            db.TaskOutputEntries.Add(_tasks.ApplyOutput(instance, seq, outputJson));

            await db.SaveChangesAsync();
        }

        // Push to streaming channel — the task controls content, format, frequency
        await runtime.WriteEventAsync(TaskOutputEventType.Output, outputJson);
    }

    private async Task CompleteInstanceAsync(Guid instanceId, TaskInstanceStatus status, SharpClawDbContext db, TaskService taskService, TaskRuntimeInstance runtime)
    {
        var instance = await db.TaskInstances.FindAsync(instanceId);
        if (instance is not null)
        {
            _tasks.ApplyTerminalStatus(instance, status);
            await db.SaveChangesAsync();
        }

        await EmitRuntimeEventPlanAsync(
            instanceId,
            _runtimeLifecycle.BuildTerminalPlan(status),
            taskService,
            runtime);
    }

    private async Task FailInstanceAsync(Guid instanceId, string error, SharpClawDbContext db, TaskService taskService, TaskRuntimeInstance runtime)
    {
        var instance = await db.TaskInstances.FindAsync(instanceId);
        if (instance is not null)
        {
            _tasks.ApplyFailure(instance, error);
            await db.SaveChangesAsync();
        }

        await EmitRuntimeEventPlanAsync(
            instanceId,
            _runtimeLifecycle.BuildFailurePlan(error),
            taskService,
            runtime);
    }



    private static async Task EmitRuntimeEventPlanAsync(
        Guid instanceId,
        TaskRuntimeEventPlan plan,
        TaskService taskService,
        TaskRuntimeInstance runtime,
        CancellationToken ct = default)
    {
        if (plan.LogMessage is not null)
            await taskService.AppendLogAsync(instanceId, plan.LogMessage, plan.LogLevel, ct);

        foreach (var evt in plan.OutputEvents)
            await runtime.WriteEventAsync(evt.Type, evt.Data, ct);
    }
    // ═══════════════════════════════════════════════════════════════

    private sealed class TaskExecutionContext(
        Guid instanceId,
        CompiledTaskPlan plan,
        TaskRuntimeInstance runtime,
        CancellationToken cancellationToken)
    {
        public Guid InstanceId { get; } = instanceId;
        public Guid ChannelId { get; set; }
        public CompiledTaskPlan Plan { get; } = plan;
        public TaskRuntimeInstance Runtime { get; } = runtime;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);
        public List<RegisteredEventHandler> EventHandlers { get; } = [];

        /// <summary>
        /// Scoped service provider for the running execution scope.  Set by
        /// the orchestrator before stepping begins; module step executors
        /// resolve services from this provider via
        /// <see cref="ITaskStepExecutionContext.Services"/>.
        /// </summary>
        public IServiceProvider Services { get; set; } = default!;
    }

    private sealed record RegisteredEventHandler(
        string ModuleTriggerKey,
        string? ParameterName,
        IReadOnlyList<ITaskStepInvocation> Body);

    // ── Module extension adapters ────────────────────────────────────

    /// <summary>
    /// Wraps <see cref="TaskExecutionContext"/> as <see cref="ITaskStepExecutionContext"/>
    /// so module step executors never reference the internal type directly.
    /// </summary>
    private sealed class TaskStepContextAdapter(
        TaskExecutionContext ctx,
        TaskOrchestrator orchestrator,
        TaskService taskService) : ITaskStepExecutionContext
    {
        public Guid InstanceId => ctx.InstanceId;
        public Guid ChannelId => ctx.ChannelId;
        public CancellationToken CancellationToken => ctx.CancellationToken;
        public IServiceProvider Services => ctx.Services;
        public IDictionary<string, object?> Variables => ctx.Variables;

        public IReadOnlyList<ITaskEventHandler> EventHandlers =>
            ctx.EventHandlers
               .Select(h => (ITaskEventHandler)new EventHandlerAdapter(h, ctx, orchestrator, taskService))
               .ToList();

        public string ResolveExpression(string expression) =>
            orchestrator._expressions.ResolveExpression(expression, ctx.Variables);

        public Task AppendLogAsync(string message) =>
            taskService.AppendLogAsync(ctx.InstanceId, message, ct: ctx.CancellationToken);

        public async Task WriteOutputAsync(string? outputJson)
        {
            using var scope = orchestrator._scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            await orchestrator.EmitOutputAsync(ctx.InstanceId, outputJson, db, ctx.Runtime);
        }

        public void SetChannelId(Guid channelId) => ctx.ChannelId = channelId;

        public Task WaitIfPausedAsync() =>
            ctx.Runtime.WaitIfPausedAsync(ctx.CancellationToken);

        public bool EvaluateCondition(string? expression) =>
            orchestrator._expressions.EvaluateCondition(expression, ctx.Variables);

        public void RegisterEventHandler(
            string moduleTriggerKey,
            string? parameterName,
            IReadOnlyList<ITaskStepInvocation> body)
        {
            ctx.EventHandlers.Add(new RegisteredEventHandler(
                moduleTriggerKey, parameterName, body));
        }

        public async Task<TaskStepResult> ExecuteStepsAsync(
            IReadOnlyList<ITaskStepInvocation> steps,
            CancellationToken cancellationToken)
        {
            using var scope = orchestrator._scopeFactory.CreateScope();
            var db   = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var ts   = scope.ServiceProvider.GetRequiredService<TaskService>();
            var chat = scope.ServiceProvider.GetService<ChatService>();
            var jobs = scope.ServiceProvider.GetService<AgentJobService>();

            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step is not TaskStepDefinition tsd)
                    throw new InvalidOperationException(
                        $"Unsupported step invocation type: {step.GetType().FullName}");
                var result = await orchestrator.ExecuteStepAsync(tsd, ctx, db, ts, chat, jobs);
                if (result == TaskStepExecutionResult.Return)
                    return TaskStepResult.Return;
            }
            return TaskStepResult.Continue;
        }
    }

    /// <summary>
    /// Wraps <see cref="RegisteredEventHandler"/> as <see cref="ITaskEventHandler"/>
    /// with a pre-bound <c>ExecuteBodyAsync</c> delegate, keeping
    /// <see cref="TaskStepDefinition"/> invisible to modules.
    /// </summary>
    private sealed class EventHandlerAdapter(
        RegisteredEventHandler handler,
        TaskExecutionContext ctx,
        TaskOrchestrator orchestrator,
        TaskService taskService) : ITaskEventHandler
    {
        public string? ModuleTriggerKey => handler.ModuleTriggerKey;
        public string? ParameterName => handler.ParameterName;

        public async Task ExecuteBodyAsync(CancellationToken ct)
        {
            // Use a fresh scope so the scoped db/services inside ExecuteStepAsync
            // remain valid even when called from a background event-loop Task.
            using var scope = orchestrator._scopeFactory.CreateScope();
            var db          = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var ts          = scope.ServiceProvider.GetRequiredService<TaskService>();
            var chat        = scope.ServiceProvider.GetService<ChatService>();
            var jobs        = scope.ServiceProvider.GetService<AgentJobService>();

            foreach (var step in handler.Body)
            {
                ct.ThrowIfCancellationRequested();
                if (step is not TaskStepDefinition tsd) continue;
                var result = await orchestrator.ExecuteStepAsync(tsd, ctx, db, ts, chat, jobs);
                if (result == TaskStepExecutionResult.Return) break;
            }
        }
    }

    private enum TaskStepExecutionResult
    {
        Continue,
        Return,
    }
}
