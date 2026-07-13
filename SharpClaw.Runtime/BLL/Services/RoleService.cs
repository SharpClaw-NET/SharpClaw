using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Runtime.BLL.Services;

/// <summary>
/// Manages roles and their permission sets.
/// </summary>
public sealed class RoleService(
    RoleAdministrationEngine administration,
    EfRoleAdministrationHost administrationHost)
{
    public async Task<IReadOnlyList<RoleResponse>> ListAsync(
        CancellationToken ct = default)
    {
        return await administration.ListAsync(
            administrationHost,
            ct);
    }

    public async Task<RoleResponse> CreateAsync(
        string name,
        CancellationToken ct = default)
    {
        return await administration.CreateAsync(
            name,
            administrationHost,
            ct);
    }

    public async Task<RoleResponse?> GetByIdAsync(
        Guid roleId,
        CancellationToken ct = default)
    {
        return await administration.GetAsync(
            roleId,
            administrationHost,
            ct);
    }

    public async Task<RolePermissionsResponse?> GetPermissionsAsync(
        Guid roleId,
        CancellationToken ct = default)
    {
        return await administration.GetPermissionsAsync(
            roleId,
            administrationHost,
            ct);
    }

    public async Task<RolePermissionsResponse?> SetPermissionsAsync(
        Guid roleId,
        SetRolePermissionsRequest request,
        Guid callerUserId,
        CancellationToken ct = default)
    {
        return await administration.SetPermissionsAsync(
            roleId,
            request,
            callerUserId,
            administrationHost,
            ct);
    }

    public async Task<RoleResponse?> RenameAsync(
        Guid roleId,
        string newName,
        CancellationToken ct = default)
    {
        return await administration.RenameAsync(
            roleId,
            newName,
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(
        Guid roleId,
        CancellationToken ct = default)
    {
        return await administration.DeleteAsync(
            roleId,
            administrationHost,
            ct);
    }
}
