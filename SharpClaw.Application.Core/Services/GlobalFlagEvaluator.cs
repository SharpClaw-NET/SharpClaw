using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Permissions;

namespace SharpClaw.Application.Services;

/// <summary>
/// Host-side implementation of <see cref="IGlobalFlagEvaluator"/> that
/// adapts <see cref="AgentActionService.EvaluateGlobalFlagByKeyAsync"/>
/// down to a single Approved/Denied verdict for module callers.
/// </summary>
public sealed class GlobalFlagEvaluator(AgentActionService agentActions)
    : IGlobalFlagEvaluator
{
    public async Task<bool> IsApprovedAsync(
        string flagKey, Guid agentId, CancellationToken ct = default)
    {
        var caller = new ActionCaller(AgentId: agentId);
        var verdict = await agentActions.EvaluateGlobalFlagByKeyAsync(
            flagKey, agentId, caller, ct: ct);
        return verdict.Verdict == ClearanceVerdict.Approved;
    }
}
