namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Host-provided entry point for starting task instances.
/// The scheduler in the AgentOrchestration module calls this to begin
/// execution of a task definition; the host is the only owner of
/// task definition validation, instance creation, and orchestration.
/// </summary>
public interface ITaskInstanceLauncher
{
    /// <summary>
    /// Create a queued instance of the supplied task definition and start
    /// the orchestrator. Returns the new instance id.
    /// </summary>
    Task<Guid> LaunchAsync(
        Guid taskDefinitionId,
        IReadOnlyDictionary<string, string>? parameterValues,
        Guid? callerAgentId,
        CancellationToken ct);
}
