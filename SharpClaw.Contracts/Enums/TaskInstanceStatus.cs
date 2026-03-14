namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Lifecycle status of a task script instance.
/// </summary>
public enum TaskInstanceStatus
{
    /// <summary>Instance created, awaiting execution start.</summary>
    Queued = 0,

    /// <summary>Task entry point is actively running.</summary>
    Running = 1,

    /// <summary>Execution temporarily suspended; can be resumed.</summary>
    Paused = 2,

    /// <summary>Entry point ran to completion successfully.</summary>
    Completed = 3,

    /// <summary>Entry point threw an unhandled exception.</summary>
    Failed = 4,

    /// <summary>Instance was cancelled by a user or agent.</summary>
    Cancelled = 5,
}
