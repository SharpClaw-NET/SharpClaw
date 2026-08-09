using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Runtime.INF.Persistence.Entities.Core.Clearance;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Permissions;
using SharpClaw.Core.State;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfRoleAdministrationHost(
    SharpClawDbContext db,
    IConfiguration configuration,
    ChatCache chatCache) : IRoleAdministrationHost
{
    private readonly CoreStateSession _states = new(db);

    public bool UniqueRoleNamesEnforced =>
        RolePermissionAdministrationEngine.IsUniqueRoleNameEnforced(
            configuration["UniqueNames:Roles"]);

    public async Task<RoleState?> LoadRoleAsync(
        Guid roleId,
        CancellationToken ct)
    {
        var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<IReadOnlyList<RoleState>> ListRolesAsync(
        CancellationToken ct)
    {
        var entities = await db.Roles
            .OrderBy(role => role.Name)
            .ToListAsync(ct);
        return _states.Map(entities);
    }

    public async Task<RoleState?> LoadRoleWithPermissionReferenceAsync(
        Guid roleId,
        CancellationToken ct)
    {
        var entity = await db.Roles
            .Include(r => r.PermissionSet)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<RoleState?> LoadRoleForDeleteAsync(
        Guid roleId,
        CancellationToken ct)
    {
        var entity = await db.Roles
            .Include(r => r.PermissionSet)
                .ThenInclude(ps => ps!.GlobalFlags)
            .Include(r => r.PermissionSet)
                .ThenInclude(ps => ps!.ResourceAccesses)
            .Include(r => r.Users)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<PermissionSetState?> LoadFullPermissionSetAsync(
        Guid permissionSetId,
        CancellationToken ct)
    {
        var entity = await db.PermissionSets
            .Include(p => p.GlobalFlags)
            .Include(p => p.ResourceAccesses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == permissionSetId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<PermissionSetState?> LoadCallerPermissionSetAsync(
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

    public void TrackRole(RoleState role)
    {
        _states.Track(role);
    }

    public void TrackPermissionSet(PermissionSetState permissionSet)
    {
        _states.Track(permissionSet);
    }

    public void ApplyRoleDeletion(RoleDeletionPlan deletion)
    {
        _states.ApplyAll();
        if (deletion.PermissionSet is not null)
        {
            foreach (var flag in deletion.GlobalFlags)
                _states.Remove(flag);
            foreach (var access in deletion.ResourceAccesses)
                _states.Remove(access);
            _states.Remove(deletion.PermissionSet);
        }

        _states.Remove(deletion.Role);
    }

    public async Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct)
    {
        await _states.SaveChangesAsync(ct);
        buildInvalidationPlan?.Invoke()?.ApplyTo(chatCache);
    }
}
