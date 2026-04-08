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
/// its own <see cref="CancellationTokenSource"/>.
/// <para>
/// The orchestrator does <b>not</b> manage persistence — it calls back
/// into <see cref="TaskService"/> for DB operations and delegates agent
/// interaction to <see cref="ChatService"/>.
/// </para>
/// </summary>
public sealed class TaskOrchestrator(
    SharpClawDbContext db,
    TaskService taskService,
    ChatService chatService,
    AgentJobService agentJobService,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Per-instance cancellation so external callers can stop a task.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>
    /// Per-instance output channels for SSE / WebSocket streaming.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, Channel<TaskOutputEvent>> _outputChannels = new();

    /// <summary>
    /// Per-instance monotonic sequence counter for output events.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, long> _sequenceCounters = new();

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

        // Prepare cancellation and output channel
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[instanceId] = cts;
        _outputChannels[instanceId] = Channel.CreateUnbounded<TaskOutputEvent>();
        _sequenceCounters[instanceId] = 0;

        // Mark as running
        instance.Status = TaskInstanceStatus.Running;
        instance.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await taskService.AppendLogAsync(instanceId, "Task started.", ct: ct);
        await PushEventAsync(instanceId, TaskOutputEventType.StatusChange, "Running");

        // Execute in background so the caller returns immediately
        _ = Task.Run(() => ExecutePlanAsync(instanceId, compilationResult.Plan, cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Request cancellation of a running instance.
    /// </summary>
    public async Task StopAsync(Guid instanceId, CancellationToken ct = default)
    {
        if (_running.TryRemove(instanceId, out var cts))
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        await taskService.CancelInstanceAsync(instanceId, ct);
    }

    /// <summary>
    /// Get a channel reader for streaming task output events.
    /// Returns <c>null</c> if no instance is running with the given ID.
    /// </summary>
    public ChannelReader<TaskOutputEvent>? GetOutputReader(Guid instanceId)
    {
        return _outputChannels.TryGetValue(instanceId, out var channel)
            ? channel.Reader
            : null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Execution engine
    // ═══════════════════════════════════════════════════════════════

    private async Task ExecutePlanAsync(
        Guid instanceId,
        CompiledTaskPlan plan,
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
        var chatService = scope.ServiceProvider.GetRequiredService<ChatService>();
        var agentJobService = scope.ServiceProvider.GetRequiredService<AgentJobService>();

        var context = new TaskExecutionContext(instanceId, plan, ct);

        // ── Set up shared data store for inter-agent communication ──
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.TaskName = plan.TaskName;
        store.TaskDescription = plan.Description;
        store.TaskSourceText = plan.Definition.SourceText;
        store.TaskParametersJson = plan.ParameterValues.Count > 0
            ? JsonSerializer.Serialize(plan.ParameterValues)
            : null;
        store.AllowedOutputFormat = plan.AgentOutputFormat;
        store.OnAgentOutput = async data => await EmitOutputAsync(instanceId, data, db);
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
            await PushEventAsync(instanceId, TaskOutputEventType.Log, $"SharedData: {description}");
        };

        // Register custom [ToolCall] hook callbacks
        foreach (var hook in plan.ToolCallHooks)
        {
            store.RegisterToolHook(hook.Name, async (args, hookCt) =>
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
                    await ExecuteStepAsync(step, context, db, taskService, chatService, agentJobService);
                }

                // Return the result variable
                if (hook.ReturnVariable is not null && context.Variables.TryGetValue(hook.ReturnVariable, out var result))
                    return result?.ToString() ?? "";
                return "";
            });
        }

        // Build custom tool definitions for ChatService
        store.CustomToolDefinitions = ChatService.BuildCustomToolDefinitions(plan.ToolCallHooks);

        try
        {
            foreach (var step in plan.ExecutionSteps)
            {
                ct.ThrowIfCancellationRequested();
                await ExecuteStepAsync(step, context, db, taskService, chatService, agentJobService);
            }

            await CompleteInstanceAsync(instanceId, TaskInstanceStatus.Completed, db, taskService);
        }
        catch (OperationCanceledException)
        {
            await CompleteInstanceAsync(instanceId, TaskInstanceStatus.Cancelled, db, taskService);
        }
        catch (Exception ex)
        {
            await FailInstanceAsync(instanceId, ex.Message, db, taskService);
        }
        finally
        {
            TaskSharedData.Remove(instanceId);

            if (_running.TryRemove(instanceId, out var cts))
                cts.Dispose();

            _sequenceCounters.TryRemove(instanceId, out _);

            if (_outputChannels.TryRemove(instanceId, out var channel))
                channel.Writer.TryComplete();
        }
    }

    private async Task ExecuteStepAsync(
        TaskStepDefinition step, TaskExecutionContext context,
        SharpClawDbContext db, TaskService taskService,
        ChatService chatService, AgentJobService agentJobService)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        switch (step.Kind)
        {
            case TaskStepKind.DeclareVariable:
                context.Variables[step.VariableName ?? ""] = step.Expression;
                break;

            case TaskStepKind.Assign:
                context.Variables[step.VariableName ?? ""] = step.Expression;
                break;

            case TaskStepKind.Log:
                var logMessage = ResolveExpression(step.Expression, context);
                await taskService.AppendLogAsync(context.InstanceId, logMessage, ct: context.CancellationToken);
                await PushEventAsync(context.InstanceId, TaskOutputEventType.Log, logMessage);
                break;

            case TaskStepKind.Delay:
                if (int.TryParse(step.Expression, out var delayMs))
                    await Task.Delay(delayMs, context.CancellationToken);
                break;

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
                break;

            case TaskStepKind.Emit:
                var outputJson = ResolveExpression(step.Expression, context);
                await EmitOutputAsync(context.InstanceId, outputJson, db);
                break;

            case TaskStepKind.Chat:
            {
                var channelId = await GetInstanceChannelIdAsync(context.InstanceId, context.CancellationToken, db);
                var message = ResolveExpression(step.Expression, context);
                Guid? agentId = step.Arguments is { Count: > 0 }
                    ? Guid.TryParse(ResolveExpression(step.Arguments[0], context), out var aid) ? aid : null
                    : null;

                var request = new ChatRequest(message, agentId, ChatClientType.API, TaskContext: new TaskChatContext(context.InstanceId, context.Plan.TaskName));
                var response = await chatService.SendMessageAsync(
                    channelId, request, ct: context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId, $"Chat → {response.AssistantMessage.Content?.Length ?? 0} chars",
                    ct: context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = response.AssistantMessage.Content;
                break;
            }

            case TaskStepKind.ChatStream:
            {
                var channelId = await GetInstanceChannelIdAsync(context.InstanceId, context.CancellationToken, db);
                var message = ResolveExpression(step.Expression, context);
                Guid? agentId = step.Arguments is { Count: > 0 }
                    ? Guid.TryParse(ResolveExpression(step.Arguments[0], context), out var aid) ? aid : null
                    : null;

                var request = new ChatRequest(message, agentId, ChatClientType.API, TaskContext: new TaskChatContext(context.InstanceId, context.Plan.TaskName));
                var sb = new StringBuilder();

                await foreach (var evt in chatService.SendMessageStreamAsync(
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
                break;
            }

            case TaskStepKind.StartTranscription:
            {
                var channelId = await GetInstanceChannelIdAsync(context.InstanceId, context.CancellationToken, db);
                var deviceIdStr = step.Arguments is { Count: > 0 }
                    ? ResolveExpression(step.Arguments[0], context)
                    : ResolveExpression(step.Expression, context);

                if (!Guid.TryParse(deviceIdStr, out var deviceId))
                    throw new InvalidOperationException($"Invalid audio device ID: {deviceIdStr}");

                var jobRequest = new SubmitAgentJobRequest(
                    AgentActionType.TranscribeFromAudioDevice,
                    ResourceId: deviceId);

                var jobResponse = await agentJobService.SubmitAsync(
                    channelId, jobRequest, context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"Started transcription job {jobResponse.Id} on device {deviceId}",
                    ct: context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = jobResponse.Id.ToString();

                StartTranscriptionEventLoop(context, jobResponse.Id);
                break;
            }

            case TaskStepKind.StopTranscription:
            {
                var jobIdStr = ResolveExpression(step.Expression, context);
                if (!Guid.TryParse(jobIdStr, out var jobId))
                    throw new InvalidOperationException($"Invalid transcription job ID: {jobIdStr}");

                await agentJobService.StopTranscriptionAsync(jobId, context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"Stopped transcription job {jobId}",
                    ct: context.CancellationToken);
                break;
            }

            case TaskStepKind.GetDefaultInputAudio:
            {
                var device = await db.InputAudios.FirstOrDefaultAsync(context.CancellationToken);
                var deviceId = device?.Id ?? Guid.Empty;

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = deviceId.ToString();
                break;
            }

            case TaskStepKind.ParseResponse:
            {
                var text = ResolveExpression(step.Expression, context);
                string parsed;
                try
                {
                    var jsonStart = text.IndexOf('{');
                    var jsonEnd = text.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var jsonText = text[jsonStart..(jsonEnd + 1)];
                        using var doc = JsonDocument.Parse(jsonText);
                        parsed = JsonSerializer.Serialize(doc.RootElement);
                    }
                    else
                    {
                        parsed = "{}";
                    }
                }
                catch (JsonException)
                {
                    parsed = "{}";
                }

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = parsed;
                break;
            }

            case TaskStepKind.HttpRequest:
            {
                var url = ResolveExpression(step.Expression, context);
                var method = step.HttpMethod?.ToUpperInvariant() ?? "GET";

                using var client = httpClientFactory.CreateClient("TaskOrchestrator");
                HttpResponseMessage httpResponse;

                if (method == "POST")
                {
                    var body = step.Arguments is { Count: > 0 }
                        ? ResolveExpression(step.Arguments[0], context)
                        : "";
                    httpResponse = await client.PostAsync(url,
                        new StringContent(body, Encoding.UTF8, "application/json"),
                        context.CancellationToken);
                }
                else
                {
                    httpResponse = await client.GetAsync(url, context.CancellationToken);
                }

                var content = await httpResponse.Content.ReadAsStringAsync(context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"HTTP {method} {url} → {(int)httpResponse.StatusCode}",
                    ct: context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = content;
                break;
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
                break;
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
                break;
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
                break;
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
                break;
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
                break;
            }

            case TaskStepKind.ChatToThread:
            {
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
                var response = await chatService.SendMessageAsync(
                    channelId, request, threadId: threadId, ct: context.CancellationToken);

                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"ChatToThread {threadId} → {response.AssistantMessage.Content?.Length ?? 0} chars",
                    ct: context.CancellationToken);

                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = response.AssistantMessage.Content;
                break;
            }

            case TaskStepKind.Conditional:
                var conditionResult = EvaluateCondition(step.Expression, context);
                var branch = conditionResult ? step.Body : step.ElseBody;
                if (branch is not null)
                {
                    foreach (var childStep in branch)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        await ExecuteStepAsync(childStep, context, db, taskService, chatService, agentJobService);
                    }
                }
                break;

            case TaskStepKind.Loop:
                while (EvaluateCondition(step.Expression, context))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (step.Body is not null)
                    {
                        foreach (var childStep in step.Body)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            await ExecuteStepAsync(childStep, context, db, taskService, chatService, agentJobService);
                        }
                    }
                }
                break;

            case TaskStepKind.EventHandler:
                context.EventHandlers.Add(new RegisteredEventHandler(
                    step.TriggerKind ?? TaskTriggerKind.TranscriptionSegment,
                    step.HandlerParameter, step.Body ?? []));
                await taskService.AppendLogAsync(
                    context.InstanceId,
                    $"Registered event handler: {step.TriggerKind}",
                    ct: context.CancellationToken);
                break;

            case TaskStepKind.Return:
                // Early exit — handled by the caller terminating iteration
                return;

            case TaskStepKind.Evaluate:
                // Generic expression — log and store result
                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = step.Expression;
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task EmitOutputAsync(Guid instanceId, string? outputJson, SharpClawDbContext db)
    {
        // Persist the latest output snapshot
        var instance = await db.TaskInstances.FindAsync(instanceId);
        if (instance is not null)
        {
            instance.OutputSnapshotJson = outputJson;

            // Persist to output history table
            var seq = _sequenceCounters.GetOrAdd(instanceId, 0) + 1;
            db.TaskOutputEntries.Add(new TaskOutputEntryDB
            {
                TaskInstanceId = instanceId,
                Sequence = seq,
                Data = outputJson,
            });

            await db.SaveChangesAsync();
        }

        // Push to streaming channel — the task controls content, format, frequency
        await PushEventAsync(instanceId, TaskOutputEventType.Output, outputJson);
    }

    private async Task CompleteInstanceAsync(Guid instanceId, TaskInstanceStatus status, SharpClawDbContext db, TaskService taskService)
    {
        var instance = await db.TaskInstances.FindAsync(instanceId);
        if (instance is not null)
        {
            instance.Status = status;
            instance.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await taskService.AppendLogAsync(instanceId, $"Task {status}.");
        await PushEventAsync(instanceId, TaskOutputEventType.StatusChange, status.ToString());
        await PushEventAsync(instanceId, TaskOutputEventType.Done, null);
    }

    private async Task FailInstanceAsync(Guid instanceId, string error, SharpClawDbContext db, TaskService taskService)
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
        await PushEventAsync(instanceId, TaskOutputEventType.StatusChange, $"Failed: {error}");
        await PushEventAsync(instanceId, TaskOutputEventType.Done, null);
    }

    /// <summary>
    /// Push a structured event to the instance's output channel.
    /// The task script has full control over <see cref="TaskOutputEventType.Output"/>
    /// events: it decides when to call <c>Emit</c>, with what data, and how often.
    /// Lifecycle events (<c>StatusChange</c>, <c>Done</c>) are added by the orchestrator.
    /// </summary>
    private Task PushEventAsync(Guid instanceId, TaskOutputEventType type, string? data)
    {
        if (!_outputChannels.TryGetValue(instanceId, out var channel))
            return Task.CompletedTask;

        var seq = _sequenceCounters.AddOrUpdate(instanceId, 1, (_, prev) => prev + 1);
        var evt = new TaskOutputEvent(type, seq, DateTimeOffset.UtcNow, data);
        return channel.Writer.WriteAsync(evt).AsTask();
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
        CancellationToken cancellationToken)
    {
        public Guid InstanceId { get; } = instanceId;
        public CompiledTaskPlan Plan { get; } = plan;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);
        public List<RegisteredEventHandler> EventHandlers { get; } = [];
    }

    private sealed record RegisteredEventHandler(
        TaskTriggerKind TriggerKind,
        string? ParameterName,
        IReadOnlyList<TaskStepDefinition> Body);
}
