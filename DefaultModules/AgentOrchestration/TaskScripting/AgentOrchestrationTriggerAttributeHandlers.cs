using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Module-owned <see cref="ITaskTriggerAttributeHandler"/> implementations
/// for the trigger-attribute family claimed by
/// <c>sharpclaw_agent_orchestration</c>:
/// <c>[Schedule]</c>, <c>[OnStartup]</c>, <c>[OnShutdown]</c>,
/// <c>[OnTaskCompleted]</c>, <c>[OnTaskFailed]</c>, <c>[OnTrigger]</c>.
/// <para>
/// Each handler returns a <see cref="TaskTriggerDefinition"/> shaped
/// identically to what the legacy core switch produced, so the parser's
/// downstream <c>BuildTriggerParameters</c> mirroring and
/// <c>[ConcurrencyPolicy]</c> override pass remain wire-compatible.
/// </para>
/// </summary>
internal static class AgentOrchestrationTriggerAttributeHandlers
{
    public static IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> All { get; } =
        new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal)
        {
            ["Schedule"]        = new ScheduleHandler(),
            ["OnStartup"]       = new OnStartupHandler(),
            ["OnShutdown"]      = new OnShutdownHandler(),
            ["OnTaskCompleted"] = new OnTaskCompletedHandler(),
            ["OnTaskFailed"]    = new OnTaskFailedHandler(),
            ["OnTrigger"]       = new OnTriggerHandler(),
            ["OnEvent"]         = new OnEventHandler(),
        };

    private sealed class OnEventHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var eventType = context.GetStringArg(0);
            if (!string.IsNullOrEmpty(eventType))
                p[AgentOrchestrationTriggerKeys.EventType] = eventType;
            var filter = context.GetNamedStringArg("Filter");
            if (!string.IsNullOrEmpty(filter))
                p[AgentOrchestrationTriggerKeys.EventFilter] = filter;
            return new TaskTriggerDefinition
            {
                TriggerKey = AgentOrchestrationTriggerKeys.Event,
                Parameters = p,
            };
        }
    }

    private sealed class ScheduleHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var expr = context.GetStringArg(0);
            if (!string.IsNullOrEmpty(expr))
                p[TaskScriptingTriggerKeys.CronExpression] = expr;
            var tz = context.GetNamedStringArg("Timezone");
            if (!string.IsNullOrEmpty(tz))
                p[TaskScriptingTriggerKeys.CronTimezone] = tz;
            return new TaskTriggerDefinition
            {
                TriggerKey = TaskScriptingTriggerKeys.Cron,
                Parameters = p,
            };
        }
    }

    private sealed class OnStartupHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context) =>
            new() { TriggerKey = TaskScriptingTriggerKeys.Startup };
    }

    private sealed class OnShutdownHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context) =>
            new() { TriggerKey = TaskScriptingTriggerKeys.Shutdown };
    }

    private sealed class OnTaskCompletedHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var src = context.GetStringArg(0);
            if (!string.IsNullOrEmpty(src))
                p[TaskScriptingTriggerKeys.SourceTaskName] = src;
            return new TaskTriggerDefinition
            {
                TriggerKey = TaskScriptingTriggerKeys.TaskCompleted,
                Parameters = p,
            };
        }
    }

    private sealed class OnTaskFailedHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var src = context.GetStringArg(0);
            if (!string.IsNullOrEmpty(src))
                p[TaskScriptingTriggerKeys.SourceTaskName] = src;
            return new TaskTriggerDefinition
            {
                TriggerKey = TaskScriptingTriggerKeys.TaskFailed,
                Parameters = p,
            };
        }
    }

    private sealed class OnTriggerHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var filter = context.GetNamedStringArg("Filter");
            if (!string.IsNullOrEmpty(filter))
                p[TaskScriptingTriggerKeys.CustomSourceFilter] = filter;
            return new TaskTriggerDefinition
            {
                TriggerKey = context.GetStringArg(0),
                Parameters = p,
            };
        }
    }
}
