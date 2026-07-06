using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Core.Permissions;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfAgentActionHost(SharpClawDbContext db) : IAgentActionHost
{
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

    public async Task<PermissionSetSnapshot?> LoadAgentPermissionSnapshotAsync(
        Guid agentId,
        CancellationToken ct)
    {
        var agent = await db.Agents
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        return agent?.Role?.PermissionSetId is { } permissionSetId
            ? await LoadPermissionSnapshotAsync(permissionSetId, ct)
            : null;
    }

    public async Task<PermissionSetSnapshot?> LoadCallerPermissionSnapshotAsync(
        ActionCaller caller,
        CancellationToken ct)
    {
        if (caller.UserId is { } userId)
        {
            var user = await db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            return user?.Role?.PermissionSetId is { } permissionSetId
                ? await LoadPermissionSnapshotAsync(permissionSetId, ct)
                : null;
        }

        if (caller.AgentId is { } callerAgentId)
        {
            var callerAgent = await db.Agents
                .Include(a => a.Role)
                .FirstOrDefaultAsync(a => a.Id == callerAgentId, ct);

            return callerAgent?.Role?.PermissionSetId is { } permissionSetId
                ? await LoadPermissionSnapshotAsync(permissionSetId, ct)
                : null;
        }

        return null;
    }

    public async Task<PermissionSetSnapshot?> LoadPermissionSnapshotAsync(
        Guid? permissionSetId,
        CancellationToken ct)
    {
        return permissionSetId is { } id
            ? await LoadPermissionSnapshotAsync(id, ct)
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
