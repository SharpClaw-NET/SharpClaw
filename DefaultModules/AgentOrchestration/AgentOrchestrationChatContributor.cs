using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.Permissions;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Module-owned <see cref="IChatProcessingContributor"/> that surfaces
/// active task definitions as agent tools when the agent has the
/// module-owned <see cref="AgentOrchestrationPermissionKeys.CanInvokeTasksAsTool"/>
/// flag granted.
/// <para>
/// The flag constant is referenced directly from the module's own
/// permission-key class — no typed accessor shim. The data path goes
/// through the narrow <see cref="ITaskToolCatalog"/> contract so the
/// module does not need to depend on <c>TaskToolProvider</c>,
/// <c>SharpClawDbContext</c>, or any infrastructure type. The clearance
/// pipeline is invoked through <see cref="IGlobalFlagEvaluator"/>.
/// </para>
/// </summary>
internal sealed class AgentOrchestrationChatContributor(
    IGlobalFlagEvaluator flagEvaluator,
    ITaskToolCatalog taskTools) : IChatProcessingContributor
{
    public async Task<IReadOnlyList<ChatToolDefinition>> GetExtraToolsAsync(
        Guid agentId, CancellationToken ct = default)
    {
        var approved = await flagEvaluator.IsApprovedAsync(
            AgentOrchestrationPermissionKeys.CanInvokeTasksAsTool, agentId, ct);

        if (!approved)
            return [];

        return await taskTools.GetToolDefinitionsAsync(ct);
    }
}
