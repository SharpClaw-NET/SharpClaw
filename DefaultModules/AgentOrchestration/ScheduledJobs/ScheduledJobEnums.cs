namespace SharpClaw.Modules.AgentOrchestration.ScheduledJobs;

/// <summary>Current lifecycle state of a scheduled job.</summary>
public enum ScheduledTaskStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Paused
}

/// <summary>Behaviour when a scheduled firing is detected to have been missed.</summary>
public enum MissedFirePolicy
{
    /// <summary>Fire once for the missed window then recompute the next occurrence.</summary>
    FireOnceAndRecompute,
    /// <summary>Skip missed firings and advance directly to the next scheduled occurrence.</summary>
    Skip
}
