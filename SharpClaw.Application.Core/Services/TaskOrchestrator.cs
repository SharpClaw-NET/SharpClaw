using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Tasks;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Compilation;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Contracts.Enums;
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
    TaskService taskService,
    ChatService chatService,
    AgentJobService agentJobService,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    TaskRuntimeHost runtimeHost)
{
    private readonly ChatService _chatService = chatService;
    private readonly AgentJobService _agentJobService = agentJobService;

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Compile and start executing a task instance.
    /// </summary>
    public async Task StartAsync(Guid instanceId, CancellationToken ct = default)
    {
        var instance = await db.TaskInstances
            .Include(i => i.TaskDefinition)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct)
            ?? throw new InvalidOperationException($"Task instance {instanceId} not found.");

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
            await db.SaveChangesAsync(ct);
            return;
        }

        // Prepare runtime entry in the host
        var runtime = runtimeHost.Register(instanceId, ct);

        if (!await taskService.TryMarkInstanceRunningAsync(instanceId, ct))
        {
            runtimeHost.Unregister(instanceId);
            throw new InvalidOperationException($"Task instance {instanceId} could not transition to Running.");
        }

        await taskService.AppendLogAsync(instanceId, "Task started.", ct: ct);
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

        var context = new TaskExecutionContext(instanceId, plan, runtime, ct);

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

        switch (step.Kind)
        {
            case TaskStepKind.DeclareVariable:
                context.Variables[step.VariableName ?? ""] = step.Expression;
                return TaskStepExecutionResult.Continue;

            case TaskStepKind.Assign:
                context.Variables[step.VariableName ?? ""] = step.Expression;
                return TaskStepExecutionResult.Continue;

            case TaskStepKind.Log:
                var logMessage = ResolveExpression(step.Expression, context);
                await taskService.AppendLogAsync(context.InstanceId, logMessage, ct: context.CancellationToken);
                await context.Runtime.WriteEventAsync(TaskOutputEventType.Log, logMessage);
                return TaskStepExecutionResult.Continue;


            case TaskStepKind.Delay:
                if (int.TryParse(step.Expression, out var delayMs))
                    await DelayWithPauseAsync(delayMs, context);
                return TaskStepExecutionResult.Continue;

            case TaskStepKind.WaitUntilStopped:
                // Block until cancellation
                try
                {
                    await Task.Delay(Timeout.Infinite, context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected — task was stopped
                }
                return TaskStepExecutionResult.Continue;

            case TaskStepKind.Emit:
                var outputJson = ResolveExpression(step.Expression, context);
                await EmitOutputAsync(context.InstanceId, outputJson, db, context.Runtime);
                return TaskStepExecutionResult.Continue;

            case TaskStepKind.Chat:
            {
                var requiredChatService = chatService ?? throw new InvalidOperationException("ChatService is not available for task chat steps.");
                var channelId = await GetInstanceChannelIdAsync(context.InstanceId, context.CancellationToken, db);
                var message = ResolveExpression(step.Expression, context);
                Guid? agentId = step.Arguments is { Count: > 0 }
                    ? Guid.TryParse(ResolveExpression(step.Arguments[0], context), out var aid) ? aid : null
                    : null;

                var request = new ChatRequest(message, agentId, ChatClientType.API, TaskContext: new TaskChatContext(context.InstanceId, context.Plan.TaskName));
                var response = await requiredChatService.SendMessageAsync(
                    channelId, request, ct: context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId, $"Chat → {response.AssistantMessage.Content?.Length ?? 0} chars",
                    ct: context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = response.AssistantMessage.Content;
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.ChatStream:
            {
                var requiredChatService = chatService ?? throw new InvalidOperationException("ChatService is not available for task chat stream steps.");
                var channelId = await GetInstanceChannelIdAsync(context.InstanceId, context.CancellationToken, db);
                var message = ResolveExpression(step.Expression, context);
                Guid? agentId = step.Arguments is { Count: > 0 }
                    ? Guid.TryParse(ResolveExpression(step.Arguments[0], context), out var aid) ? aid : null
                    : null;

                var request = new ChatRequest(message, agentId, ChatClientType.API, TaskContext: new TaskChatContext(context.InstanceId, context.Plan.TaskName));
                var sb = new StringBuilder();

                await foreach (var evt in requiredChatService.SendMessageStreamAsync(
                    channelId, request, AutoApproveAsync, ct: context.CancellationToken))
                {
                    if (evt.Type == ChatStreamEventType.TextDelta && evt.Delta is not null)
                        sb.Append(evt.Delta);
                }

                await taskService.AppendLogAsync(
                    context.InstanceId, $"ChatStream → {sb.Length} chars",
                    ct: context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = sb.ToString();
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.StartTranscription:
            {
                var requiredAgentJobService = agentJobService ?? throw new InvalidOperationException("AgentJobService is not available for task transcription steps.");
                var channelId = await GetInstanceChannelIdAsync(context.InstanceId, context.CancellationToken, db);
                var deviceIdStr = step.Arguments is { Count: > 0 }
                    ? ResolveExpression(step.Arguments[0], context)
                    : ResolveExpression(step.Expression, context);

                if (!Guid.TryParse(deviceIdStr, out var deviceId))
                    throw new InvalidOperationException($"Invalid audio device ID: {deviceIdStr}");

                var jobRequest = new SubmitAgentJobRequest(
                    ActionKey: "transcribe_from_audio_device",
                    ResourceId: deviceId);

                var jobResponse = await requiredAgentJobService.SubmitAsync(
                    channelId, jobRequest, context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"Started transcription job {jobResponse.Id} on device {deviceId}",
                    ct: context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = jobResponse.Id.ToString();

                StartTranscriptionEventLoop(context, jobResponse.Id);
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.StopTranscription:
            {
                var requiredAgentJobService = agentJobService ?? throw new InvalidOperationException("AgentJobService is not available for task transcription steps.");
                var jobIdStr = ResolveExpression(step.Expression, context);
                if (!Guid.TryParse(jobIdStr, out var jobId))
                    throw new InvalidOperationException($"Invalid transcription job ID: {jobIdStr}");

                await requiredAgentJobService.StopTranscriptionAsync(jobId, context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"Stopped transcription job {jobId}",
                    ct: context.CancellationToken);
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.GetDefaultInputAudio:
            {
                var device = await db.InputAudios.FirstOrDefaultAsync(context.CancellationToken);
                var deviceId = device?.Id ?? Guid.Empty;

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = deviceId.ToString();
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.ParseResponse:
            {
                var text = ResolveExpression(step.Expression, context);
                var parsed = ParseStructuredResponse(text, step.TypeName, context.Plan.Definition);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = parsed;
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.HttpRequest:
            {
                var url = ResolveExpression(step.Expression, context);
                var method = step.HttpMethod?.ToUpperInvariant() ?? "GET";

                using var client = httpClientFactory.CreateClient("TaskOrchestrator");
                using var request = CreateHttpRequestMessage(step, context, method, url);
                using var httpResponse = await client.SendAsync(request, context.CancellationToken);

                var content = await httpResponse.Content.ReadAsStringAsync(context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"HTTP {method} {url} → {(int)httpResponse.StatusCode}",
                    ct: context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = content;
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.FindModel:
            {
                var search = ResolveExpression(step.Expression, context);
                var model = await db.Models
                    .FirstOrDefaultAsync(m => m.CustomId == search || m.Name == search, context.CancellationToken);
                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = model?.Id.ToString();
                await taskService.AppendLogAsync(
                    context.InstanceId, $"FindModel '{search}' → {(model is not null ? model.Id : "not found")}",
                    ct: context.CancellationToken);
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.FindProvider:
            {
                var search = ResolveExpression(step.Expression, context);
                var provider = await db.Providers
                    .FirstOrDefaultAsync(p => p.CustomId == search || p.Name == search, context.CancellationToken);
                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = provider?.Id.ToString();
                await taskService.AppendLogAsync(
                    context.InstanceId, $"FindProvider '{search}' → {(provider is not null ? provider.Id : "not found")}",
                    ct: context.CancellationToken);
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.FindAgent:
            {
                var search = ResolveExpression(step.Expression, context);
                var agent = await db.Agents
                    .FirstOrDefaultAsync(a => a.CustomId == search || a.Name == search, context.CancellationToken);
                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = agent?.Id.ToString();
                await taskService.AppendLogAsync(
                    context.InstanceId, $"FindAgent '{search}' → {(agent is not null ? agent.Id : "not found")}",
                    ct: context.CancellationToken);
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.CreateAgent:
            {
                // Arguments: [0]=name, [1]=modelId, optional [2]=systemPrompt, optional [3]=customId
                var name = step.Arguments is { Count: > 0 }
                    ? ResolveExpression(step.Arguments[0], context) : "Task Agent";
                var modelIdStr = step.Arguments is { Count: > 1 }
                    ? ResolveExpression(step.Arguments[1], context) : null;
                var systemPrompt = step.Arguments is { Count: > 2 }
                    ? ResolveExpression(step.Arguments[2], context) : null;
                var customId = step.Arguments is { Count: > 3 }
                    ? ResolveExpression(step.Arguments[3], context) : null;

                Guid modelId = Guid.TryParse(modelIdStr, out var mid) ? mid : Guid.Empty;

                // Upsert: if a customId is provided and an agent with that
                // customId already exists, update it instead of creating a
                // duplicate.  When multiple matches exist, pick the most
                // recently created one.
                SharpClaw.Infrastructure.Models.AgentDB? agentEntity = null;
                if (!string.IsNullOrEmpty(customId))
                {
                    agentEntity = await db.Agents
                        .Where(a => a.CustomId == customId)
                        .OrderByDescending(a => a.CreatedAt)
                        .FirstOrDefaultAsync(context.CancellationToken);
                }

                if (agentEntity is not null)
                {
                    agentEntity.Name = name;
                    agentEntity.ModelId = modelId;
                    agentEntity.SystemPrompt = systemPrompt;
                    await db.SaveChangesAsync(context.CancellationToken);
                }
                else
                {
                    agentEntity = new SharpClaw.Infrastructure.Models.AgentDB
                    {
                        Name = name,
                        ModelId = modelId,
                        SystemPrompt = systemPrompt,
                        CustomId = customId,
                    };
                    db.Agents.Add(agentEntity);
                    await db.SaveChangesAsync(context.CancellationToken);
                }

                // Auto-add the new agent to the task's channel AllowedAgents
                // so subsequent Chat/ChatStream steps can use it.
                var createAgentChannelId = await GetInstanceChannelIdAsync(
                    context.InstanceId, context.CancellationToken, db);
                var createAgentChannel = await db.Channels
                    .Include(c => c.AllowedAgents)
                    .FirstOrDefaultAsync(c => c.Id == createAgentChannelId, context.CancellationToken);
                if (createAgentChannel is not null &&
                    !createAgentChannel.AllowedAgents.Any(a => a.Id == agentEntity.Id))
                {
                    createAgentChannel.AllowedAgents.Add(agentEntity);
                    await db.SaveChangesAsync(context.CancellationToken);
                }

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = agentEntity.Id.ToString();
                await taskService.AppendLogAsync(
                    context.InstanceId, $"CreateAgent '{name}' → {agentEntity.Id}",
                    ct: context.CancellationToken);
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.CreateThread:
            {
                // Arguments: [0]=channelId (or "channel" to use instance channel), optional [1]=name
                var channelIdStr = step.Arguments is { Count: > 0 }
                    ? ResolveExpression(step.Arguments[0], context) : null;
                var threadName = step.Arguments is { Count: > 1 }
                    ? ResolveExpression(step.Arguments[1], context) : null;

                Guid threadChannelId;
                if (Guid.TryParse(channelIdStr, out var cid))
                    threadChannelId = cid;
                else
                    threadChannelId = await GetInstanceChannelIdAsync(context.InstanceId, context.CancellationToken, db);

                var threadEntity = new ChatThreadDB
                {
                    Name = threadName ?? $"Task Thread {DateTimeOffset.UtcNow:HH:mm}",
                    ChannelId = threadChannelId,
                };
                db.ChatThreads.Add(threadEntity);
                await db.SaveChangesAsync(context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = threadEntity.Id.ToString();
                await taskService.AppendLogAsync(
                    context.InstanceId, $"CreateThread '{threadEntity.Name}' → {threadEntity.Id}",
                    ct: context.CancellationToken);
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.ChatToThread:
            {
                var requiredChatService = chatService ?? throw new InvalidOperationException("ChatService is not available for task thread chat steps.");
                // Arguments: [0]=threadId, [1]=message (Expression), optional [2]=agentId
                var threadIdStr = step.Arguments is { Count: > 0 }
                    ? ResolveExpression(step.Arguments[0], context) : null;
                var message = ResolveExpression(step.Expression, context);
                Guid? agentId = step.Arguments is { Count: > 2 }
                    ? Guid.TryParse(ResolveExpression(step.Arguments[2], context), out var aid) ? aid : null
                    : null;

                if (!Guid.TryParse(threadIdStr, out var threadId))
                    throw new InvalidOperationException($"Invalid thread ID: {threadIdStr}");

                var channelId = await GetInstanceChannelIdAsync(context.InstanceId, context.CancellationToken, db);
                var request = new ChatRequest(message, agentId, ChatClientType.API, TaskContext: new TaskChatContext(context.InstanceId, context.Plan.TaskName));
                var response = await requiredChatService.SendMessageAsync(
                    channelId, request, threadId: threadId, ct: context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"ChatToThread {threadId} → {response.AssistantMessage.Content?.Length ?? 0} chars",
                    ct: context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = response.AssistantMessage.Content;
                return TaskStepExecutionResult.Continue;
            }

            case TaskStepKind.Conditional:
                var conditionResult = EvaluateCondition(step.Expression, context);
                var branch = conditionResult ? step.Body : step.ElseBody;
                if (branch is not null)
                {
                    foreach (var childStep in branch)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        var childResult = await ExecuteStepAsync(childStep, context, db, taskService, chatService, agentJobService);
                        if (childResult == TaskStepExecutionResult.Return)
                        {
                            return TaskStepExecutionResult.Return;
                        }
                    }
                }
                return TaskStepExecutionResult.Continue;

            case TaskStepKind.Loop:
                var loopKind = step.LoopKind ?? (step.VariableName is not null ? TaskLoopKind.ForEach : TaskLoopKind.While);
                if (loopKind == TaskLoopKind.ForEach)
                {
                    foreach (var item in EnumerateLoopValues(step, context))
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        await WaitIfPausedAsync(context);
                        if (step.VariableName is not null)
                        {
                            context.Variables[step.VariableName] = item;
                        }

                        if (step.Body is null)
                        {
                            continue;
                        }

                        foreach (var childStep in step.Body)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            var childResult = await ExecuteStepAsync(childStep, context, db, taskService, chatService, agentJobService);
                            if (childResult == TaskStepExecutionResult.Return)
                            {
                                return TaskStepExecutionResult.Return;
                            }
                        }
                    }

                    return TaskStepExecutionResult.Continue;
                }

                while (EvaluateCondition(step.Expression, context))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(context);
                    if (step.Body is not null)
                    {
                        foreach (var childStep in step.Body)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            var childResult = await ExecuteStepAsync(childStep, context, db, taskService, chatService, agentJobService);
                            if (childResult == TaskStepExecutionResult.Return)
                            {
                                return TaskStepExecutionResult.Return;
                            }
                        }
                    }
                }

                return TaskStepExecutionResult.Continue;

            case TaskStepKind.EventHandler:
                context.EventHandlers.Add(new RegisteredEventHandler(
                    step.TriggerKind ?? TaskTriggerKind.TranscriptionSegment,
                    step.HandlerParameter, step.Body ?? []));
                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"Registered event handler: {step.TriggerKind}",
                    ct: context.CancellationToken);
                return TaskStepExecutionResult.Continue;

            case TaskStepKind.Return:
                return TaskStepExecutionResult.Return;

            case TaskStepKind.Evaluate:
                // Generic expression — log and store result
                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = step.Expression;
                return TaskStepExecutionResult.Continue;
        }

        return TaskStepExecutionResult.Continue;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static IEnumerable<object?> EnumerateLoopValues(TaskStepDefinition step, TaskExecutionContext context)
    {
        var resolved = ResolveExpression(step.Expression, context);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            yield break;
        }

        if (context.Variables.TryGetValue(resolved, out var variableValue))
        {
            foreach (var item in EnumerateValue(variableValue))
            {
                yield return item;
            }

            yield break;
        }

        foreach (var item in EnumerateValue(resolved))
        {
            yield return item;
        }
    }

    private static IEnumerable<object?> EnumerateValue(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string text)
        {
            if (TryEnumerateJsonArray(text, out var items))
            {
                foreach (var item in items)
                {
                    yield return item;
                }

                yield break;
            }

            yield return text;
            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
        }
    }

    private static bool TryEnumerateJsonArray(string text, out List<object?> values)
    {
        values = [];
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                values.Add(item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.GetRawText());
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ParseStructuredResponse(string text, string? typeName, TaskScriptDefinition definition)
    {
        var jsonText = ExtractJsonObject(text);
        if (jsonText is null)
        {
            throw new InvalidOperationException("ParseResponse expected a JSON object in the source text.");
        }

        using var doc = JsonDocument.Parse(jsonText);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("ParseResponse expected a JSON object payload.");
        }

        ValidateParsedResponseShape(doc.RootElement, typeName, definition);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    private static string? ExtractJsonObject(string text)
    {
        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        return jsonStart >= 0 && jsonEnd > jsonStart
            ? text[jsonStart..(jsonEnd + 1)]
            : null;
    }

    private static void ValidateParsedResponseShape(JsonElement element, string? typeName, TaskScriptDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return;
        }

        var dataType = definition.DataTypes.FirstOrDefault(dt => dt.Name == typeName);
        if (dataType is null)
        {
            return;
        }

        foreach (var property in dataType.Properties)
        {
            if (!element.TryGetProperty(property.Name, out var propertyElement))
            {
                throw new InvalidOperationException($"ParseResponse<{typeName}> missing property '{property.Name}'.");
            }

            ValidateParsedProperty(propertyElement, property);
        }
    }

    private static void ValidateParsedProperty(JsonElement value, TaskPropertyDefinition property)
    {
        if (property.IsCollection)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Property '{property.Name}' must be a JSON array.");
            }

            return;
        }

        if (!IsCompatibleJsonValue(value, property.TypeName))
        {
            throw new InvalidOperationException($"Property '{property.Name}' does not match declared type '{property.TypeName}'.");
        }
    }

    private static bool IsCompatibleJsonValue(JsonElement value, string typeName)
    {
        var normalizedType = typeName.TrimEnd('?');
        return normalizedType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "int" or "long" or "double" or "decimal" => value.ValueKind == JsonValueKind.Number,
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "Guid" or "DateTime" or "DateTimeOffset" or "TimeSpan" => value.ValueKind == JsonValueKind.String,
            _ => value.ValueKind == JsonValueKind.Object
        };
    }

    private static HttpRequestMessage CreateHttpRequestMessage(TaskStepDefinition step, TaskExecutionContext context, string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT")
        {
            var body = step.Arguments is { Count: > 1 }
                ? ResolveExpression(step.Arguments[1], context)
                : step.Arguments is { Count: > 0 }
                    ? ResolveExpression(step.Arguments[0], context)
                    : string.Empty;
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static Task WaitIfPausedAsync(TaskExecutionContext context)
        => context.Runtime.WaitIfPausedAsync(context.CancellationToken);

    private async Task DelayWithPauseAsync(int delayMs, TaskExecutionContext context)
    {
        const int chunkMs = 250;
        var remaining = delayMs;
        while (remaining > 0)
        {
            await WaitIfPausedAsync(context);
            var nextDelay = Math.Min(chunkMs, remaining);
            await Task.Delay(nextDelay, context.CancellationToken);
            remaining -= nextDelay;
        }
    }

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
    // Instance helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve the <see cref="TaskInstanceDB.ChannelId"/> for a running instance.
    /// Chat and transcription steps require a channel.
    /// </summary>
    private async Task<Guid> GetInstanceChannelIdAsync(Guid instanceId, CancellationToken ct, SharpClawDbContext db)
    {
        var channelId = await db.TaskInstances
            .Where(i => i.Id == instanceId)
            .Select(i => i.ChannelId)
            .FirstOrDefaultAsync(ct);

        return channelId
            ?? throw new InvalidOperationException(
                $"Task instance {instanceId} has no ChannelId. " +
                "Set ChannelId when starting the instance for Chat/Transcription steps.");
    }

    /// <summary>
    /// Auto-deny tool-call jobs that require approval during task execution.
    /// Tasks only use permissions pre-granted in the channel; when a job
    /// needs additional approval it is automatically cancelled.  The
    /// cancelled status propagates as a tool-result error — if the task
    /// script does not handle it the orchestrator marks the instance as
    /// failed.
    /// </summary>
    private static Task<bool> AutoApproveAsync(AgentJobResponse _, CancellationToken __) =>
        Task.FromResult(false);

    /// <summary>
    /// Start a background loop that reads transcription segments from
    /// <see cref="AgentJobService.Subscribe"/> and fires all matching
    /// <see cref="RegisteredEventHandler"/>s in the execution context.
    /// </summary>
    private void StartTranscriptionEventLoop(TaskExecutionContext context, Guid jobId)
    {
        _ = Task.Run(async () =>
        {
            using var loopScope = scopeFactory.CreateScope();
            var db = loopScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var taskService = loopScope.ServiceProvider.GetRequiredService<TaskService>();
            var chatService = loopScope.ServiceProvider.GetRequiredService<ChatService>();
            var agentJobService = loopScope.ServiceProvider.GetRequiredService<AgentJobService>();

            try
            {
                var reader = agentJobService.Subscribe(jobId);
                if (reader is null) return;

                await foreach (var segment in reader.ReadAllAsync(context.CancellationToken))
                {
                    var handlers = context.EventHandlers
                        .Where(h => h.TriggerKind == TaskTriggerKind.TranscriptionSegment)
                        .ToList();

                    foreach (var handler in handlers)
                    {
                        if (handler.ParameterName is not null)
                        {
                            context.Variables[handler.ParameterName] =
                                JsonSerializer.Serialize(new { segment.Text, segment.StartTime, segment.EndTime, segment.Confidence });
                            context.Variables[handler.ParameterName + ".Text"] = segment.Text;
                        }

                        foreach (var step in handler.Body)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            await ExecuteStepAsync(step, context, db, taskService, chatService, agentJobService);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"Event loop error: {ex.Message}",
                    "Error");
            }
        }, CancellationToken.None);
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
        public CompiledTaskPlan Plan { get; } = plan;
        public TaskRuntimeInstance Runtime { get; } = runtime;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);
        public List<RegisteredEventHandler> EventHandlers { get; } = [];
    }

    private sealed record RegisteredEventHandler(
        TaskTriggerKind TriggerKind,
        string? ParameterName,
        IReadOnlyList<TaskStepDefinition> Body);

    private enum TaskStepExecutionResult
    {
        Continue,
        Return,
    }
}
