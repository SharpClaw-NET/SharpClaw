namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// A registered event handler within a running task instance.
/// The handler body is exposed as a pre-bound async delegate so modules
/// can fire it without referencing <c>TaskStepDefinition</c>.
/// </summary>
public interface ITaskEventHandler
{
    /// <summary>
    /// Module-owned trigger key. Used by module event loops to match
    /// handlers without referencing host-side discriminators.
    /// </summary>
    string? ModuleTriggerKey { get; }

    string? ParameterName { get; }

    /// <summary>
    /// Execute the handler body steps within the current task context.
    /// </summary>
    Task ExecuteBodyAsync(CancellationToken ct);
}

