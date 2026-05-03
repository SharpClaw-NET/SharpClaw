namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Outcome of executing a single task step.  Returned by
/// <see cref="ITaskStepInvocationExecutor"/> implementations to signal
/// whether the orchestrator should continue with the next sibling step
/// or unwind to the task entry point.
/// </summary>
public enum TaskStepResult
{
    /// <summary>Continue with the next sibling step.</summary>
    Continue,

    /// <summary>
    /// Unwind out of the current step list (and any nesting) up to the
    /// task entry point.  Used by the scripting <c>return</c> primitive.
    /// </summary>
    Return,
}
