using System.Text.Json;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Module-side executor that owns the Task Scripting language primitives:
/// declare/assign, evaluate, log, delay, wait-until-stopped, return, and the
/// control-flow constructs (conditional, loop, event handler).
/// <para>
/// Implements <see cref="ITaskStepInvocationExecutor"/> so it can drive
/// nested control flow through <see cref="ITaskStepExecutionContext.ExecuteStepsAsync"/>
/// while remaining oblivious to the orchestrator's internal types.
/// </para>
/// </summary>
public sealed class TaskScriptingStepExecutor : ITaskStepInvocationExecutor
{
    public string ModuleId => "sharpclaw_agent_orchestration";

    public bool CanExecute(string moduleStepKey) => moduleStepKey switch
    {
        TaskScriptingStepKeys.DeclareVariable
            or TaskScriptingStepKeys.Assign
            or TaskScriptingStepKeys.Log
            or TaskScriptingStepKeys.Delay
            or TaskScriptingStepKeys.WaitUntilStopped
            or TaskScriptingStepKeys.Conditional
            or TaskScriptingStepKeys.Loop
            or TaskScriptingStepKeys.EventHandler
            or TaskScriptingStepKeys.Return
            or TaskScriptingStepKeys.Evaluate => true,
        _ => false,
    };

    /// <summary>
    /// Resolved-argument path is unused — every Task Scripting primitive needs
    /// raw step access (nested bodies, unresolved expressions, handler bodies).
    /// The orchestrator routes us through <see cref="ExecuteInvocationAsync"/>.
    /// </summary>
    public Task<bool> ExecuteAsync(
        string moduleStepKey,
        ITaskStepExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable) => Task.FromResult(true);

    public async Task<TaskStepResult> ExecuteInvocationAsync(
        ITaskStepInvocation step,
        ITaskStepExecutionContext context)
    {
        switch (step.StepKey)
        {
            case TaskScriptingStepKeys.DeclareVariable:
            case TaskScriptingStepKeys.Assign:
                context.Variables[step.VariableName ?? ""] = step.RawExpression;
                return TaskStepResult.Continue;

            case TaskScriptingStepKeys.Evaluate:
                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = step.RawExpression;
                return TaskStepResult.Continue;

            case TaskScriptingStepKeys.Log:
            {
                var message = step.RawExpression is null
                    ? string.Empty
                    : context.ResolveExpression(step.RawExpression);
                await context.AppendLogAsync(message);
                return TaskStepResult.Continue;
            }

            case TaskScriptingStepKeys.Delay:
            {
                var resolved = step.RawExpression is null
                    ? null
                    : context.ResolveExpression(step.RawExpression);
                if (int.TryParse(resolved, out var delayMs))
                    await DelayWithPauseAsync(delayMs, context);
                return TaskStepResult.Continue;
            }

            case TaskScriptingStepKeys.WaitUntilStopped:
                try
                {
                    await Task.Delay(Timeout.Infinite, context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected — task was stopped.
                }
                return TaskStepResult.Continue;

            case TaskScriptingStepKeys.Return:
                return TaskStepResult.Return;

            case TaskScriptingStepKeys.Conditional:
            {
                var branch = context.EvaluateCondition(step.RawExpression)
                    ? step.Body
                    : step.ElseBody;
                if (branch is null) return TaskStepResult.Continue;
                return await context.ExecuteStepsAsync(branch, context.CancellationToken);
            }

            case TaskScriptingStepKeys.Loop:
                return await ExecuteLoopAsync(step, context);

            case TaskScriptingStepKeys.EventHandler:
            {
                if (step.ModuleTriggerKey is null)
                    throw new InvalidOperationException(
                        "EventHandler step requires a ModuleTriggerKey.");
                context.RegisterEventHandler(
                    step.ModuleTriggerKey,
                    step.HandlerParameter,
                    step.Body ?? []);
                await context.AppendLogAsync(
                    $"Registered event handler: {step.ModuleTriggerKey}");
                return TaskStepResult.Continue;
            }
        }

        return TaskStepResult.Continue;
    }

    // ── Loop helpers ─────────────────────────────────────────────────

    private static async Task<TaskStepResult> ExecuteLoopAsync(
        ITaskStepInvocation step, ITaskStepExecutionContext context)
    {
        var ct = context.CancellationToken;
        var isForEach = step.VariableName is not null;

        if (isForEach)
        {
            foreach (var item in EnumerateLoopValues(step, context))
            {
                ct.ThrowIfCancellationRequested();
                await context.WaitIfPausedAsync();
                if (step.VariableName is not null)
                    context.Variables[step.VariableName] = item;
                if (step.Body is null) continue;
                var result = await context.ExecuteStepsAsync(step.Body, ct);
                if (result == TaskStepResult.Return) return TaskStepResult.Return;
            }
            return TaskStepResult.Continue;
        }

        while (context.EvaluateCondition(step.RawExpression))
        {
            ct.ThrowIfCancellationRequested();
            await context.WaitIfPausedAsync();
            if (step.Body is null) continue;
            var result = await context.ExecuteStepsAsync(step.Body, ct);
            if (result == TaskStepResult.Return) return TaskStepResult.Return;
        }
        return TaskStepResult.Continue;
    }

    private static IEnumerable<object?> EnumerateLoopValues(
        ITaskStepInvocation step, ITaskStepExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(step.RawExpression))
            yield break;

        // Prefer the raw variable lookup so non-string sequences (lists,
        // arrays) iterate without going through the string-only resolver.
        if (context.Variables.TryGetValue(step.RawExpression, out var direct))
        {
            foreach (var item in EnumerateValue(direct))
                yield return item;
            yield break;
        }

        var resolved = context.ResolveExpression(step.RawExpression);
        if (string.IsNullOrWhiteSpace(resolved))
            yield break;

        if (context.Variables.TryGetValue(resolved, out var variableValue))
        {
            foreach (var item in EnumerateValue(variableValue))
                yield return item;
            yield break;
        }

        foreach (var item in EnumerateValue(resolved))
            yield return item;
    }

    private static IEnumerable<object?> EnumerateValue(object? value)
    {
        if (value is null) yield break;

        if (value is string text)
        {
            if (TryEnumerateJsonArray(text, out var items))
            {
                foreach (var item in items) yield return item;
                yield break;
            }
            yield return text;
            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable) yield return item;
        }
    }

    private static bool TryEnumerateJsonArray(string text, out List<object?> values)
    {
        values = [];
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;
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

    private static async Task DelayWithPauseAsync(int delayMs, ITaskStepExecutionContext context)
    {
        const int chunkMs = 250;
        var remaining = delayMs;
        while (remaining > 0)
        {
            await context.WaitIfPausedAsync();
            var nextDelay = Math.Min(chunkMs, remaining);
            await Task.Delay(nextDelay, context.CancellationToken);
            remaining -= nextDelay;
        }
    }
}
