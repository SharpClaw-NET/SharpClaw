using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Permissions;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class EfRoleAdministrationHost(
    SharpClawDbContext db,
    IConfiguration configuration,
    ChatCache chatCache) : IRoleAdministrationHost
{
    public bool UniqueRoleNamesEnforced =>
        RolePermissionAdministrationEngine.IsUniqueRoleNameEnforced(
            configuration["UniqueNames:Roles"]);

    public async Task<RoleDB?> LoadRoleAsync(
        Guid roleId,
        CancellationToken ct)
    {
        return await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
    }

    public async Task<IReadOnlyList<RoleDB>> ListRolesAsync(
        CancellationToken ct)
    {
        return await db.Roles
            .OrderBy(role => role.Name)
            .ToListAsync(ct);
    }

    public async Task<RoleDB?> LoadRoleWithPermissionReferenceAsync(
        Guid roleId,
        CancellationToken ct)
    {
        return await db.Roles
            .Include(r => r.PermissionSet)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);
    }

    public async Task<RoleDB?> LoadRoleForDeleteAsync(
        Guid roleId,
        CancellationToken ct)
    {
        return await db.Roles
            .Include(r => r.PermissionSet)
                .ThenInclude(ps => ps!.GlobalFlags)
            .Include(r => r.PermissionSet)
                .ThenInclude(ps => ps!.ResourceAccesses)
            .Include(r => r.Users)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);
    }

    public async Task<PermissionSetDB?> LoadFullPermissionSetAsync(
        Guid permissionSetId,
        CancellationToken ct)
    {
        return await db.PermissionSets
            .Include(p => p.GlobalFlags)
            .Include(p => p.ResourceAccesses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == permissionSetId, ct);
    }

    public async Task<PermissionSetDB?> LoadCallerPermissionSetAsync(
        Guid userId,
        CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Role?.PermissionSetId is not { } permissionSetId)
            return null;

        return await LoadFullPermissionSetAsync(permissionSetId, ct);
    }

    public async Task<bool> IsUserAdminAsync(
        Guid userId,
        CancellationToken ct)
    {
        return await db.Users.AnyAsync(
            u => u.Id == userId && u.IsUserAdmin,
            ct);
    }

    public async Task<IReadOnlyList<string>> ListRoleNamesAsync(
        Guid? excludeId,
        CancellationToken ct)
    {
        return await db.Roles
            .Where(r => excludeId == null || r.Id != excludeId)
            .Select(r => r.Name)
            .ToListAsync(ct);
    }

    public void TrackRole(RoleDB role)
    {
        db.Roles.Add(role);
    }

    public void TrackPermissionSet(PermissionSetDB permissionSet)
    {
        db.PermissionSets.Add(permissionSet);
    }

    public void ApplyRoleDeletion(RoleDeletionPlan deletion)
    {
        if (deletion.PermissionSet is not null)
        {
            db.RemoveRange(deletion.GlobalFlags);
            db.RemoveRange(deletion.ResourceAccesses);
            db.PermissionSets.Remove(deletion.PermissionSet);
        }

        db.Roles.Remove(deletion.Role);
    }

    public async Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        buildInvalidationPlan?.Invoke()?.ApplyTo(chatCache);
    }
}
