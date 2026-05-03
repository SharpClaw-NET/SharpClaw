namespace SharpClaw.Contracts.Permissions;

/// <summary>
/// Narrow, decision-shaped contract that lets modules evaluate
/// global-flag permissions against the host without taking a direct
/// dependency on core services such as <c>AgentActionService</c>.
/// <para>
/// Each module owns the constant string for its own flags (see the
/// per-module <c>*PermissionKeys</c> classes) and asks this evaluator
/// for a single Approved/Denied verdict. The evaluator hides the
/// underlying clearance pipeline; modules never see
/// <c>ClearanceVerdict</c>, caller layers, or
/// <c>AgentActionService</c> internals.
/// </para>
/// </summary>
public interface IGlobalFlagEvaluator
{
    /// <summary>
    /// Returns <see langword="true"/> when the agent has the named
    /// global flag granted at a clearance level that resolves to
    /// <c>Approved</c> for the current caller layer; otherwise
    /// <see langword="false"/>.
    /// </summary>
    Task<bool> IsApprovedAsync(
        string flagKey, Guid agentId, CancellationToken ct = default);
}
