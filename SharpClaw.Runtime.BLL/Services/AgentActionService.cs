using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class AgentActionService(
    ModuleRegistry registry,
    AgentActionWorkflowEngine actionWorkflow,
    EfAgentActionHost actionHost)
{
    public async Task<AgentActionResult> EvaluateGlobalFlagByKeyAsync(
        string flagKey,
        Guid agentId,
        ActionCaller caller,
        Func<Task>? onApproved = null,
        CancellationToken ct = default,
        Guid? channelPsId = null,
        Guid? contextPsId = null)
    {
        var result = await actionWorkflow.EvaluateGlobalFlagByKeyAsync(
            flagKey,
            agentId,
            caller,
            actionHost,
            channelPsId,
            contextPsId,
            ct);

        if (result.Verdict == Contracts.Enums.ClearanceVerdict.Approved
            && onApproved is not null)
        {
            await onApproved();
        }

        return result;
    }

    public Task<AgentActionResult>? TryEvaluateByDelegateNameAsync(
        string delegateName,
        Guid agentId,
        Guid? resourceId,
        ActionCaller caller,
        CancellationToken ct = default,
        Guid? channelPsId = null,
        Guid? contextPsId = null)
    {
        return actionWorkflow.TryEvaluateByDelegateNameAsync(
            delegateName,
            agentId,
            resourceId,
            caller,
            registry,
            actionHost,
            channelPsId,
            contextPsId,
            ct);
    }

    public bool HasGrantByDelegateName(
        PermissionSetDB permissionSet,
        string delegateName,
        Guid? resourceId)
    {
        var snapshot = PermissionSetSnapshot.FromPermissionSet(permissionSet);
        return actionWorkflow.HasGrantByDelegateName(
            snapshot,
            delegateName,
            resourceId,
            registry);
    }

    public async Task<PermissionSetDB?> LoadPermissionSetAsync(
        Guid permissionSetId,
        CancellationToken ct)
    {
        return await actionHost.LoadPermissionSetAsync(permissionSetId, ct);
    }
}
