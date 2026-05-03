namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Trigger-key constants owned by the <c>sharpclaw_agent_orchestration</c>
/// module. String values are persisted verbatim in
/// <c>TaskTriggerBindingDB.Kind</c> and in serialized task scripts.
/// </summary>
public static class AgentOrchestrationTriggerKeys
{
    /// <summary>Generic host-bus event with a <c>SharpClawEventType</c> filter.</summary>
    public const string Event         = "Event";

    // Parameter names persisted into TaskTriggerDefinition.Parameters.
    // Preserved verbatim to remain wire-compatible with serialized scripts.

    /// <summary>Comma-separated list of <c>SharpClawEventType</c> flag names.</summary>
    public const string EventType   = "EventType";

    /// <summary>Optional substring filter applied to <c>SourceId</c> / <c>Summary</c>.</summary>
    public const string EventFilter = "EventFilter";
}
