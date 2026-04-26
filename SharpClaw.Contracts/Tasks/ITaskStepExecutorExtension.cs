namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Allows a module to handle task steps whose <see cref="TaskStepDefinition"/>
/// <c>StepKey</c> is owned by this module. Registered at startup via
/// <c>TaskScriptParser.RegisterModule</c> and injected into the
/// orchestrator as <c>IEnumerable&lt;ITaskStepExecutorExtension&gt;</c>.
/// The orchestrator routes any step whose key is not a well-known core key
/// to the extension that claims it via <see cref="CanExecute"/>.
/// </summary>
public interface ITaskStepExecutorExtension
{
    /// <summary>The module ID this extension belongs to.</summary>
    string ModuleId { get; }

    /// <summary>
    /// Returns <c>true</c> if this extension handles the given module step key.
    /// </summary>
    bool CanExecute(string moduleStepKey);

    /// <summary>
    /// Execute the step. Returns <c>true</c> to continue task execution;
    /// <c>false</c> signals an early return from the task body.
    /// Throw to propagate a step execution error.
    /// </summary>
    /// <param name="moduleStepKey">The module-owned step key being executed.</param>
    /// <param name="context">Execution context — variables, event handlers, logging.</param>
    /// <param name="arguments">Positional step arguments already resolved to string values.</param>
    /// <param name="expression">Step expression string, if any (already resolved).</param>
    /// <param name="resultVariable">Variable name to store the step's output, or <c>null</c>.</param>
    Task<bool> ExecuteAsync(
        string moduleStepKey,
        ITaskStepExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable);
}

