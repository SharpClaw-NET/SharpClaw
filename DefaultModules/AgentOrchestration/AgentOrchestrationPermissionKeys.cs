namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Module-owned global-flag keys for the agent-orchestration module.
/// </summary>
public static class AgentOrchestrationPermissionKeys
{
    /// <summary>
    /// Grants permission to invoke task definitions as agent tools.
    /// Agents with this flag see active task definitions in their tool list.
    /// </summary>
    public const string CanInvokeTasksAsTool = "CanInvokeTasksAsTool";
}
