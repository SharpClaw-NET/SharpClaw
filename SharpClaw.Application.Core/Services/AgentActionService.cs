using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Application.Services;

/// <summary>
/// Host adapter for high-level agent action permission checks. The application
/// owns EF loading and approved callbacks; SharpClaw.Core owns the permission
/// and clearance decision semantics.
/// </summary>
public sealed class AgentActionService(
    SharpClawDbContext db,
    ModuleRegistry registry,
    PermissionEvaluationEngine permissionEvaluator)
{
    /// <summary>
    /// Evaluate a global-flag permission by its canonical key
    /// (e.g. "CanClickDesktop"). The flag must exist in the agent's role
    /// permission set, and Core resolves channel/context/role precedence plus
    /// caller approval requirements.
    /// </summary>
    public async Task<AgentActionResult> EvaluateGlobalFlagByKeyAsync(
        string flagKey,
        Guid agentId,
        ActionCaller caller,
        Func<Task>? onApproved = null,
        CancellationToken ct = default,
        Guid? channelPsId = null,
        Guid? contextPsId = null)
    {
        var agentPermissions = await LoadAgentPermissionSnapshotAsync(agentId, ct);
        var channelPermissions = await LoadPermissionSnapshotAsync(channelPsId, ct);
        var contextPermissions = await LoadPermissionSnapshotAsync(contextPsId, ct);
        var callerPermissions = await LoadCallerPermissionSnapshotAsync(caller, ct);

        var result = permissionEvaluator.EvaluateGlobalFlag(
            flagKey,
            agentPermissions,
            channelPermissions,
            contextPermissions,
            callerPermissions,
            caller);

        if (result.Verdict == Contracts.Enums.ClearanceVerdict.Approved && onApproved is not null)
            await onApproved();

        return result;
    }

    /// <summary>
    /// Evaluates a permission check by delegate-method name.
    /// Global flags are resolved dynamically via <see cref="ModuleRegistry.ResolveGlobalFlag"/>;
    /// per-resource delegates via <see cref="ModuleRegistry.ResolveResourceType"/>.
    /// Returns <c>null</c> if <paramref name="delegateName"/> is not recognised.
    /// </summary>
    public Task<AgentActionResult>? TryEvaluateByDelegateNameAsync(
        string delegateName,
        Guid agentId,
        Guid? resourceId,
        ActionCaller caller,
        CancellationToken ct = default,
        Guid? channelPsId = null,
        Guid? contextPsId = null)
    {
        var flagKey = registry.ResolveGlobalFlag(delegateName);
        if (flagKey is not null)
        {
            return EvaluateGlobalFlagByKeyAsync(
                flagKey,
                agentId,
                caller,
                ct: ct,
                channelPsId: channelPsId,
                contextPsId: contextPsId);
        }

        var resourceType = registry.ResolveResourceType(delegateName);
        if (resourceType is not null && resourceId.HasValue)
        {
            return EvaluateResourceAccessAsync(
                agentId,
                resourceId.Value,
                resourceType,
                caller,
                $"{resourceType} access",
                ct: ct,
                channelPsId: channelPsId,
                contextPsId: contextPsId);
        }

        return null;
    }

    /// <summary>
    /// Checks whether a permission set contains a grant that matches the given
    /// delegate-method name and optional resource ID. Used by channel
    /// pre-authorization for module actions.
    /// </summary>
    public bool HasGrantByDelegateName(
        PermissionSetDB ps,
        string delegateName,
        Guid? resourceId)
    {
        var snapshot = PermissionSetSnapshot.FromPermissionSet(ps);
        var flagKey = registry.ResolveGlobalFlag(delegateName);
        if (flagKey is not null)
            return PermissionEvaluationEngine.HasGlobalFlagGrant(snapshot, flagKey);

        var resourceType = registry.ResolveResourceType(delegateName);
        return resourceType is not null
            && PermissionEvaluationEngine.HasResourceGrant(snapshot, resourceType, resourceId);
    }

    /// <summary>
    /// Loads a full <see cref="PermissionSetDB"/> by its primary key,
    /// including the unified resource access collection and whitelists.
    /// </summary>
    public async Task<PermissionSetDB?> LoadPermissionSetAsync(
        Guid permissionSetId,
        CancellationToken ct)
    {
        return await db.PermissionSets
            .Include(p => p.GlobalFlags)
            .Include(p => p.ResourceAccesses)
            .Include(p => p.ClearanceUserWhitelist)
            .Include(p => p.ClearanceAgentWhitelist)
            .FirstOrDefaultAsync(p => p.Id == permissionSetId, ct);
    }

    private async Task<AgentActionResult> EvaluateResourceAccessAsync(
        Guid agentId,
        Guid resourceId,
        string resourceType,
        ActionCaller caller,
        string resourceDescription,
        Func<Task>? onApproved = null,
        CancellationToken ct = default,
        Guid? channelPsId = null,
        Guid? contextPsId = null)
    {
        var agentPermissions = await LoadAgentPermissionSnapshotAsync(agentId, ct);
        var channelPermissions = await LoadPermissionSnapshotAsync(channelPsId, ct);
        var contextPermissions = await LoadPermissionSnapshotAsync(contextPsId, ct);
        var callerPermissions = await LoadCallerPermissionSnapshotAsync(caller, ct);

        var result = permissionEvaluator.EvaluateResourceAccess(
            resourceType,
            resourceId,
            resourceDescription,
            agentPermissions,
            channelPermissions,
            contextPermissions,
            callerPermissions,
            caller);

        if (result.Verdict == Contracts.Enums.ClearanceVerdict.Approved && onApproved is not null)
            await onApproved();

        return result;
    }

    private async Task<PermissionSetSnapshot?> LoadAgentPermissionSnapshotAsync(
        Guid agentId,
        CancellationToken ct)
    {
        var agent = await db.Agents
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        return agent?.Role?.PermissionSetId is { } psId
            ? await LoadPermissionSnapshotAsync(psId, ct)
            : null;
    }

    private async Task<PermissionSetSnapshot?> LoadCallerPermissionSnapshotAsync(
        ActionCaller caller,
        CancellationToken ct)
    {
        if (caller.UserId is { } userId)
        {
            var user = await db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            return user?.Role?.PermissionSetId is { } userPsId
                ? await LoadPermissionSnapshotAsync(userPsId, ct)
                : null;
        }

        if (caller.AgentId is { } callerAgentId)
        {
            var callerAgent = await db.Agents
                .Include(a => a.Role)
                .FirstOrDefaultAsync(a => a.Id == callerAgentId, ct);

            return callerAgent?.Role?.PermissionSetId is { } agentPsId
                ? await LoadPermissionSnapshotAsync(agentPsId, ct)
                : null;
        }

        return null;
    }

    private async Task<PermissionSetSnapshot?> LoadPermissionSnapshotAsync(
        Guid? permissionSetId,
        CancellationToken ct)
    {
        return permissionSetId is { } psId
            ? await LoadPermissionSnapshotAsync(psId, ct)
            : null;
    }

    private async Task<PermissionSetSnapshot?> LoadPermissionSnapshotAsync(
        Guid permissionSetId,
        CancellationToken ct)
    {
        var permissionSet = await LoadPermissionSetAsync(permissionSetId, ct);
        return permissionSet is null
            ? null
            : PermissionSetSnapshot.FromPermissionSet(permissionSet);
    }
}
