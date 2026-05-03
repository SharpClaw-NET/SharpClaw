namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Public projection of a running task instance's execution context.
/// Passed to <see cref="ITaskStepExecutorExtension"/> implementations so modules
/// can read and write task variables, enumerate event handlers, and execute step bodies
/// without taking a dependency on the internal orchestrator or Infrastructure.Tasks.
/// </summary>
public interface ITaskStepExecutionContext
{
    /// <summary>The running task instance ID.</summary>
    Guid InstanceId { get; }

    /// <summary>The channel this task instance is executing against.</summary>
    Guid ChannelId { get; }

    /// <summary>Active cancellation token for the task instance.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Scoped service provider for the running task instance.  Modules may
    /// resolve services (e.g. <c>SharpClawDbContext</c>, chat/agent-job
    /// services) from this provider when executing steps.  The scope is
    /// owned by the orchestrator and is valid for the duration of step
    /// execution.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Task-script variables. Modules may read and write entries to propagate
    /// results (e.g. a transcription job ID stored via <c>ResultVariable</c>).
    /// </summary>
    IDictionary<string, object?> Variables { get; }

    /// <summary>Registered event handlers in this task instance.</summary>
    IReadOnlyList<ITaskEventHandler> EventHandlers { get; }

    /// <summary>
    /// Resolve an expression string (variable reference or literal) to its
    /// current value within this context.
    /// </summary>
    string ResolveExpression(string expression);

    /// <summary>
    /// Append a log entry to this task instance's log.
    /// </summary>
    Task AppendLogAsync(string message);

    /// <summary>
    /// Push an output payload to the task instance's output stream
    /// (persisted as a <c>TaskOutputEntry</c> and surfaced to streaming
    /// listeners). Used by modules implementing the <c>core.emit</c>
    /// step or any equivalent module-owned output operation.
    /// </summary>
    Task WriteOutputAsync(string? outputJson);

    /// <summary>
    /// Update the channel currently associated with this running task
    /// instance.  Used by module executors implementing
    /// <c>core.create_channel</c> when the task was started in
    /// context-mode (no initial channel) — subsequent chat/thread steps
    /// then resolve to the newly-created channel.
    /// </summary>
    void SetChannelId(Guid channelId);

    /// <summary>
    /// Recursively execute a nested step list (loop body, then/else branch,
    /// event-handler body).  Returns <see cref="TaskStepResult.Return"/> if
    /// any nested step requested an early return so the caller can unwind.
    /// </summary>
    Task<TaskStepResult> ExecuteStepsAsync(
        IReadOnlyList<ITaskStepInvocation> steps,
        CancellationToken cancellationToken);

    /// <summary>
    /// Evaluate a boolean expression against the current variable scope.
    /// Used by control-flow executors (conditional, while-loop).
    /// </summary>
    bool EvaluateCondition(string? expression);

    /// <summary>
    /// Register an event handler for a module-owned trigger key.  Used by
    /// the scripting <c>event_handler</c> step; module event loops then
    /// enumerate <see cref="EventHandlers"/> to fire matching handlers.
    /// </summary>
    void RegisterEventHandler(
        string moduleTriggerKey,
        string? parameterName,
        IReadOnlyList<ITaskStepInvocation> body);

    /// <summary>
    /// Block until the task instance has been resumed (no-op when not paused).
    /// </summary>
    Task WaitIfPausedAsync();
}
