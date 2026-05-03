using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Compilation;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;

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
    IEnumerable<ITaskStepExecutorExtension> stepExtensions)
{
    private readonly IReadOnlyList<ITaskStepExecutorExtension> _stepExtensions = [.. stepExtensions];
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Compile and start executing a task instance.
    /// </summary>
    public async Task StartAsync(Guid instanceId, CancellationToken ct = default)
    {
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
            instance.Status = TaskInstanceStatus.Failed;
            instance.ErrorMessage = $"Compilation failed: {errors}";
            instance.CompletedAt = DateTimeOffset.UtcNow;
            await startupDb.SaveChangesAsync(ct);
            return;
        }

        // Prepare runtime entry in the host
        var runtime = runtimeHost.Register(instanceId, ct);

        if (!await startupTaskService.TryMarkInstanceRunningAsync(instanceId, ct))
        {
            runtimeHost.Unregister(instanceId);
            throw new InvalidOperationException($"Task instance {instanceId} could not transition to Running.");
        }

        await startupTaskService.AppendLogAsync(instanceId, "Task started.", ct: ct);
        await runtime.WriteEventAsync(TaskOutputEventType.StatusChange, "Running");

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
            await taskService.AppendLogAsync(instanceId, $"SharedData: {description}");
            await runtime.WriteEventAsync(TaskOutputEventType.Log, $"SharedData: {description}");
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
        }
        catch (OperationCanceledException)
        {
            await CompleteInstanceAsync(instanceId, TaskInstanceStatus.Cancelled, db, taskService, runtime);
        }
        catch (Exception ex)
        {
            await FailInstanceAsync(instanceId, ex.Message, db, taskService, runtime);
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

        // Invocation-aware path: raw step access for control-flow primitives.
        if (executor is ITaskStepInvocationExecutor invocationExecutor)
        {
            var result = await invocationExecutor.ExecuteInvocationAsync(step, moduleCtx);
            return result == TaskStepResult.Return
                ? TaskStepExecutionResult.Return
                : TaskStepExecutionResult.Continue;
        }

        // Resolved-argument path: traditional module steps that consume runtime values.
        var resolvedArgs = step.Arguments?.Select(a => ResolveExpression(a, context)).ToList();
        if (step.TypeName is not null)
        {
            resolvedArgs ??= [];
            resolvedArgs.Insert(0, step.TypeName);
        }
        var resolvedExpr = step.Expression is not null ? ResolveExpression(step.Expression, context) : null;
        var keepGoing = await executor.ExecuteAsync(stepKey, moduleCtx, resolvedArgs, resolvedExpr, step.ResultVariable);
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
            instance.OutputSnapshotJson = outputJson;

            // Persist to output history table
            var seq = runtime.IncrementSequence();
            db.TaskOutputEntries.Add(new TaskOutputEntryDB
            {
                TaskInstanceId = instanceId,
                Sequence = seq,
                Data = outputJson,
            });

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
            instance.Status = status;
            instance.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await taskService.AppendLogAsync(instanceId, $"Task {status}.");
        await runtime.WriteEventAsync(TaskOutputEventType.StatusChange, status.ToString());
        await runtime.WriteEventAsync(TaskOutputEventType.Done, null);
    }

    private async Task FailInstanceAsync(Guid instanceId, string error, SharpClawDbContext db, TaskService taskService, TaskRuntimeInstance runtime)
    {
        var instance = await db.TaskInstances.FindAsync(instanceId);
        if (instance is not null)
        {
            instance.Status = TaskInstanceStatus.Failed;
            instance.ErrorMessage = error;
            instance.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await taskService.AppendLogAsync(instanceId, $"Task failed: {error}", "Error");
        await runtime.WriteEventAsync(TaskOutputEventType.StatusChange, $"Failed: {error}");
        await runtime.WriteEventAsync(TaskOutputEventType.Done, null);
    }



    private static string ResolveExpression(string? expression, TaskExecutionContext context)
    {
        if (expression is null) return "";

        // Variable substitution — longest names first to avoid partial matches
        foreach (var (name, value) in context.Variables.OrderByDescending(kv => kv.Key.Length))
        {
            expression = expression.Replace(name, value?.ToString() ?? "");
        }

        // Property access on JSON values: resolve "varName.PropertyName"
        expression = Regex.Replace(expression, @"(\w+)\.(\w+)", match =>
        {
            var varName = match.Groups[1].Value;
            var propName = match.Groups[2].Value;
            if (context.Variables.TryGetValue(varName, out var val) && val is string json)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty(propName, out var prop))
                        return prop.ValueKind == JsonValueKind.String
                            ? prop.GetString() ?? ""
                            : prop.GetRawText();
                }
                catch (JsonException) { }
            }
            return match.Value;
        });

        // Evaluate C# string-literal quotes and '+' concatenation.
        // The parser extracts raw C# syntax (e.g. "hello"), but the
        // runtime value should be hello (without quotes).
        // Handles: "text", "a" + variable, "a" + "b" + variable
        if (expression.Contains(" + "))
        {
            var parts = expression.Split(" + ");
            var sb = new StringBuilder(expression.Length);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                sb.Append(trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
                    ? trimmed[1..^1]
                    : trimmed);
            }
            expression = sb.ToString();
        }
        else if (expression.Length >= 2 && expression[0] == '"' && expression[^1] == '"')
        {
            expression = expression[1..^1];
        }

        return expression;
    }

    private static bool EvaluateCondition(string? expression, TaskExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var resolved = ResolveExpression(expression, context);

        // Literal booleans
        if (string.Equals(resolved, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(resolved, "false", StringComparison.OrdinalIgnoreCase)) return false;

        // Null checks: "x != null", "x == null"
        if (resolved.EndsWith("!= null", StringComparison.Ordinal))
        {
            var val = resolved[..^7].Trim();
            return !string.IsNullOrEmpty(val) && val != "null";
        }
        if (resolved.EndsWith("== null", StringComparison.Ordinal))
        {
            var val = resolved[..^7].Trim();
            return string.IsNullOrEmpty(val) || val == "null";
        }

        // Comparison operators: ==, !=, >=, <=, >, <
        foreach (var op in new[] { "!=", "==", ">=", "<=", ">", "<" })
        {
            var idx = resolved.IndexOf(op, StringComparison.Ordinal);
            if (idx < 0) continue;

            var left = resolved[..idx].Trim();
            var right = resolved[(idx + op.Length)..].Trim();

            // Numeric comparison
            if (double.TryParse(left, out var lNum) && double.TryParse(right, out var rNum))
            {
                return op switch
                {
                    "==" => Math.Abs(lNum - rNum) < 0.0001,
                    "!=" => Math.Abs(lNum - rNum) >= 0.0001,
                    ">"  => lNum > rNum,
                    "<"  => lNum < rNum,
                    ">=" => lNum >= rNum,
                    "<=" => lNum <= rNum,
                    _    => false
                };
            }

            // String comparison
            return op switch
            {
                "==" => string.Equals(left, right, StringComparison.Ordinal),
                "!=" => !string.Equals(left, right, StringComparison.Ordinal),
                _    => false
            };
        }

        // Truthy: non-empty string
        return !string.IsNullOrEmpty(resolved);
    }

    // ═══════════════════════════════════════════════════════════════
    // Execution context
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
            TaskOrchestrator.ResolveExpression(expression, ctx);

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
            TaskOrchestrator.EvaluateCondition(expression, ctx);

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
