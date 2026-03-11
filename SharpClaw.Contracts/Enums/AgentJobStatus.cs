namespace SharpClaw.Contracts.Enums;

/// <summary>
/// Lifecycle status of an agent action job.
/// </summary>
public enum AgentJobStatus
{
    /// <summary>Job created, permission check in progress.</summary>
    Queued = 0,

    /// <summary>Permission granted, action is running.</summary>
    Executing = 1,

    /// <summary>Agent has the capability but clearance requires approval
    /// from an authorised user or agent before execution can proceed.</summary>
    AwaitingApproval = 2,

    /// <summary>Action finished successfully; result and logs are available.</summary>
    Completed = 3,

    /// <summary>Action threw an error; error log is available.</summary>
    Failed = 4,

    /// <summary>Agent does not hold the required permission at all.</summary>
    Denied = 5,

    /// <summary>Job was cancelled by a user or agent.</summary>
    Cancelled = 6,

    /// <summary>Job is temporarily paused; can be resumed.</summary>
    Paused = 7
}
