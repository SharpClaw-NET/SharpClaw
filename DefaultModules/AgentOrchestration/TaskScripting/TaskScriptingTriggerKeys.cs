namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Trigger-key constants owned by the <c>sharpclaw_task_scripting</c> module.
/// String values are persisted verbatim in <c>TaskTriggerBindingDB.Kind</c>
/// and in serialized task scripts.
/// </summary>
public static class TaskScriptingTriggerKeys
{
    /// <summary>Fires on a cron schedule.</summary>
    public const string Cron = "Cron";

    /// <summary>Fires once when the host application has fully started.</summary>
    public const string Startup  = "Startup";

    /// <summary>Fires once when the host application begins shutting down.</summary>
    public const string Shutdown = "Shutdown";

    /// <summary>Fires when another task definition completes successfully.</summary>
    public const string TaskCompleted = "TaskCompleted";

    /// <summary>Fires when another task definition fails.</summary>
    public const string TaskFailed    = "TaskFailed";

    // Parameter names persisted into TaskTriggerDefinition.Parameters.
    // Preserved verbatim to remain wire-compatible with serialized scripts.

    /// <summary>
    /// Source task name persisted on <c>TaskCompleted</c>/<c>TaskFailed</c>
    /// bindings as the binding-row discriminator.
    /// </summary>
    public const string SourceTaskName = "SourceTaskName";

    /// <summary>Cron expression for <c>[Schedule]</c> bindings.</summary>
    public const string CronExpression = "CronExpression";

    /// <summary>Cron timezone for <c>[Schedule]</c> bindings.</summary>
    public const string CronTimezone = "CronTimezone";

    /// <summary>Filter parameter for <c>[OnTrigger]</c> bindings.</summary>
    public const string CustomSourceFilter = "CustomSourceFilter";
}
