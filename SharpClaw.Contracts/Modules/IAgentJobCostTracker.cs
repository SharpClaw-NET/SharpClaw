namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Host-side token accounting exposed to modules without requiring a
/// reference to Core or Infrastructure.
/// </summary>
public interface IAgentJobCostTracker
{
    /// <summary>
    /// Adds prompt and completion tokens to the specified agent job.
    /// </summary>
    Task RecordTokensAsync(
        Guid jobId,
        int promptTokens,
        int completionTokens,
        CancellationToken ct = default);

    /// <summary>
    /// Adds prompt and completion tokens to the specified agent job context.
    /// </summary>
    Task RecordTokensAsync(
        AgentJobContext job,
        int promptTokens,
        int completionTokens,
        CancellationToken ct = default) =>
        RecordTokensAsync(job.JobId, promptTokens, completionTokens, ct);
}
