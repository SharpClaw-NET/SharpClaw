namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Optional companion interface to <see cref="ITaskStepExecutorExtension"/>
/// for executors that need raw step access (nested bodies, unresolved
/// expressions, event-handler registration).  When the orchestrator is
/// dispatching a step whose <c>StepKey</c> is claimed by an executor
/// implementing this interface, it bypasses the resolved-argument path
/// and calls <see cref="ExecuteInvocationAsync"/> directly.
/// </summary>
public interface ITaskStepInvocationExecutor : ITaskStepExecutorExtension
{
    /// <summary>
    /// Execute a step with full access to its raw shape.  Return
    /// <see cref="TaskStepResult.Return"/> to unwind to the task entry point.
    /// </summary>
    Task<TaskStepResult> ExecuteInvocationAsync(
        ITaskStepInvocation step,
        ITaskStepExecutionContext context);
}
