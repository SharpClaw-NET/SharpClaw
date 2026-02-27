using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages roles and their permission sets. Enforces the rule that a
/// user can only grant permissions they already hold themselves.
/// </summary>
public sealed class RoleService(SharpClawDbContext db)
{
    // ═══════════════════════════════════════════════════════════════
    // Read
    // ═══════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<RoleResponse>> ListAsync(CancellationToken ct = default)
    {
        return await db.Roles
            .OrderBy(r => r.Name)
            .Select(r => new RoleResponse(r.Id, r.Name, r.PermissionSetId))
            .ToListAsync(ct);
    }

    public async Task<RolePermissionsResponse?> GetPermissionsAsync(
        Guid roleId, CancellationToken ct = default)
    {
        var role = await db.Roles
            .Include(r => r.PermissionSet)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);

        if (role is null)
            return null;

        var ps = role.PermissionSet is not null
            ? await LoadFullPermissionSetAsync(role.PermissionSet.Id, ct)
            : null;

        return ToResponse(role, ps);
    }

    // ═══════════════════════════════════════════════════════════════
    // Set permissions
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Replaces the entire permission set of <paramref name="roleId"/>
    /// with the values in <paramref name="request"/>. The calling user
    /// (<paramref name="callerUserId"/>) must hold every permission
    /// they are granting — you cannot give what you don't have.
    /// </summary>
    public async Task<RolePermissionsResponse?> SetPermissionsAsync(
        Guid roleId, SetRolePermissionsRequest request,
        Guid callerUserId, CancellationToken ct = default)
    {
        var role = await db.Roles
            .Include(r => r.PermissionSet)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);

        if (role is null)
            return null;

        // Load caller's own permission set for validation.
        var callerPs = await LoadCallerPermissionSetAsync(callerUserId, ct);

        ValidateGlobalFlags(request, callerPs);
        ValidateResourceGrants(request, callerPs);

        // Replace the permission set (delete old access entries, rebuild).
        PermissionSetDB ps;
        if (role.PermissionSetId is { } existingPsId)
        {
            ps = await LoadFullPermissionSetAsync(existingPsId, ct)
                ?? throw new InvalidOperationException(
                    $"Permission set {existingPsId} not found.");

            // Clear existing grant collections.
            ps.DangerousShellAccesses.Clear();
            ps.SafeShellAccesses.Clear();
            ps.ContainerAccesses.Clear();
            ps.WebsiteAccesses.Clear();
            ps.SearchEngineAccesses.Clear();
            ps.LocalInfoStorePermissions.Clear();
            ps.ExternalInfoStorePermissions.Clear();
            ps.AudioDeviceAccesses.Clear();
            ps.AgentPermissions.Clear();
            ps.TaskPermissions.Clear();
            ps.SkillPermissions.Clear();
        }
        else
        {
            ps = new PermissionSetDB();
            db.PermissionSets.Add(ps);
            await db.SaveChangesAsync(ct);
            role.PermissionSetId = ps.Id;
        }

        // Apply global flags.
        ps.DefaultClearance = request.DefaultClearance;
        ps.CanCreateSubAgents = request.CanCreateSubAgents;
        ps.CanCreateContainers = request.CanCreateContainers;
        ps.CanRegisterInfoStores = request.CanRegisterInfoStores;
        ps.CanAccessLocalhostInBrowser = request.CanAccessLocalhostInBrowser;
        ps.CanAccessLocalhostCli = request.CanAccessLocalhostCli;

        // Apply per-resource grants.
        AddGrants(ps.DangerousShellAccesses, request.DangerousShellAccesses,
            (g, psId) => new DangerousShellAccessDB
            { PermissionSetId = psId, SystemUserId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.SafeShellAccesses, request.SafeShellAccesses,
            (g, psId) => new SafeShellAccessDB
            { PermissionSetId = psId, ContainerId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.ContainerAccesses, request.ContainerAccesses,
            (g, psId) => new ContainerAccessDB
            { PermissionSetId = psId, ContainerId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.WebsiteAccesses, request.WebsiteAccesses,
            (g, psId) => new WebsiteAccessDB
            { PermissionSetId = psId, WebsiteId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.SearchEngineAccesses, request.SearchEngineAccesses,
            (g, psId) => new SearchEngineAccessDB
            { PermissionSetId = psId, SearchEngineId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.LocalInfoStorePermissions, request.LocalInfoStoreAccesses,
            (g, psId) => new LocalInfoStoreAccessDB
            { PermissionSetId = psId, LocalInformationStoreId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.ExternalInfoStorePermissions, request.ExternalInfoStoreAccesses,
            (g, psId) => new ExternalInfoStoreAccessDB
            { PermissionSetId = psId, ExternalInformationStoreId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.AudioDeviceAccesses, request.AudioDeviceAccesses,
            (g, psId) => new AudioDeviceAccessDB
            { PermissionSetId = psId, AudioDeviceId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.AgentPermissions, request.AgentAccesses,
            (g, psId) => new AgentManagementAccessDB
            { PermissionSetId = psId, AgentId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.TaskPermissions, request.TaskAccesses,
            (g, psId) => new TaskManageAccessDB
            { PermissionSetId = psId, ScheduledTaskId = g.ResourceId, Clearance = g.Clearance });

        AddGrants(ps.SkillPermissions, request.SkillAccesses,
            (g, psId) => new SkillManageAccessDB
            { PermissionSetId = psId, SkillId = g.ResourceId, Clearance = g.Clearance });

        await db.SaveChangesAsync(ct);

        return ToResponse(role, ps);
    }

    // ═══════════════════════════════════════════════════════════════
    // Validation — you can only grant what you hold
    // ═══════════════════════════════════════════════════════════════

    private static void ValidateGlobalFlags(
        SetRolePermissionsRequest request, PermissionSetDB? callerPs)
    {
        if (callerPs is null)
        {
            if (request is
                {
                    CanCreateSubAgents: false,
                    CanCreateContainers: false,
                    CanRegisterInfoStores: false,
                    CanAccessLocalhostInBrowser: false,
                    CanAccessLocalhostCli: false
                })
                return;

            throw new UnauthorizedAccessException(
                "You have no permissions — cannot grant any global flags.");
        }

        if (request.CanCreateSubAgents && !callerPs.CanCreateSubAgents)
            throw new UnauthorizedAccessException(
                "Cannot grant CanCreateSubAgents — you do not hold this permission.");

        if (request.CanCreateContainers && !callerPs.CanCreateContainers)
            throw new UnauthorizedAccessException(
                "Cannot grant CanCreateContainers — you do not hold this permission.");

        if (request.CanRegisterInfoStores && !callerPs.CanRegisterInfoStores)
            throw new UnauthorizedAccessException(
                "Cannot grant CanRegisterInfoStores — you do not hold this permission.");

        if (request.CanAccessLocalhostInBrowser && !callerPs.CanAccessLocalhostInBrowser)
            throw new UnauthorizedAccessException(
                "Cannot grant CanAccessLocalhostInBrowser — you do not hold this permission.");

        if (request.CanAccessLocalhostCli && !callerPs.CanAccessLocalhostCli)
            throw new UnauthorizedAccessException(
                "Cannot grant CanAccessLocalhostCli — you do not hold this permission.");
    }

    private static void ValidateResourceGrants(
        SetRolePermissionsRequest request, PermissionSetDB? callerPs)
    {
        ValidateCollection("DangerousShellAccesses", request.DangerousShellAccesses,
            callerPs?.DangerousShellAccesses, a => a.SystemUserId);
        ValidateCollection("SafeShellAccesses", request.SafeShellAccesses,
            callerPs?.SafeShellAccesses, a => a.ContainerId);
        ValidateCollection("ContainerAccesses", request.ContainerAccesses,
            callerPs?.ContainerAccesses, a => a.ContainerId);
        ValidateCollection("WebsiteAccesses", request.WebsiteAccesses,
            callerPs?.WebsiteAccesses, a => a.WebsiteId);
        ValidateCollection("SearchEngineAccesses", request.SearchEngineAccesses,
            callerPs?.SearchEngineAccesses, a => a.SearchEngineId);
        ValidateCollection("LocalInfoStoreAccesses", request.LocalInfoStoreAccesses,
            callerPs?.LocalInfoStorePermissions, a => a.LocalInformationStoreId);
        ValidateCollection("ExternalInfoStoreAccesses", request.ExternalInfoStoreAccesses,
            callerPs?.ExternalInfoStorePermissions, a => a.ExternalInformationStoreId);
        ValidateCollection("AudioDeviceAccesses", request.AudioDeviceAccesses,
            callerPs?.AudioDeviceAccesses, a => a.AudioDeviceId);
        ValidateCollection("AgentAccesses", request.AgentAccesses,
            callerPs?.AgentPermissions, a => a.AgentId);
        ValidateCollection("TaskAccesses", request.TaskAccesses,
            callerPs?.TaskPermissions, a => a.ScheduledTaskId);
        ValidateCollection("SkillAccesses", request.SkillAccesses,
            callerPs?.SkillPermissions, a => a.SkillId);
    }

    /// <summary>
    /// For each requested grant, the caller must hold either the exact
    /// resource ID or the <see cref="WellKnownIds.AllResources"/> wildcard.
    /// </summary>
    private static void ValidateCollection<TAccess>(
        string name,
        IReadOnlyList<ResourceGrant>? requested,
        ICollection<TAccess>? callerGrants,
        Func<TAccess, Guid> resourceSelector)
    {
        if (requested is null or { Count: 0 })
            return;

        if (callerGrants is null or { Count: 0 })
            throw new UnauthorizedAccessException(
                $"Cannot grant {name} — you hold no grants of this type.");

        var callerIds = new HashSet<Guid>(callerGrants.Select(resourceSelector));
        var hasWildcard = callerIds.Contains(WellKnownIds.AllResources);

        foreach (var grant in requested)
        {
            if (hasWildcard)
                continue;

            if (!callerIds.Contains(grant.ResourceId))
                throw new UnauthorizedAccessException(
                    $"Cannot grant {name} for resource {grant.ResourceId} " +
                    "— you do not hold this permission.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static void AddGrants<TAccess>(
        ICollection<TAccess> collection,
        IReadOnlyList<ResourceGrant>? grants,
        Func<ResourceGrant, Guid, TAccess> factory)
    {
        if (grants is null)
            return;

        foreach (var g in grants)
            collection.Add(factory(g, Guid.Empty)); // psId set by EF navigation
    }

    private async Task<PermissionSetDB?> LoadCallerPermissionSetAsync(
        Guid userId, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Role?.PermissionSetId is not { } psId)
            return null;

        return await LoadFullPermissionSetAsync(psId, ct);
    }

    private async Task<PermissionSetDB?> LoadFullPermissionSetAsync(
        Guid psId, CancellationToken ct)
    {
        return await db.PermissionSets
            .Include(p => p.DangerousShellAccesses)
            .Include(p => p.SafeShellAccesses)
            .Include(p => p.ContainerAccesses)
            .Include(p => p.WebsiteAccesses)
            .Include(p => p.SearchEngineAccesses)
            .Include(p => p.LocalInfoStorePermissions)
            .Include(p => p.ExternalInfoStorePermissions)
            .Include(p => p.AudioDeviceAccesses)
            .Include(p => p.AgentPermissions)
            .Include(p => p.TaskPermissions)
            .Include(p => p.SkillPermissions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == psId, ct);
    }

    private static RolePermissionsResponse ToResponse(RoleDB role, PermissionSetDB? ps) =>
        new(
            RoleId: role.Id,
            RoleName: role.Name,
            DefaultClearance: ps?.DefaultClearance ?? PermissionClearance.Unset,
            CanCreateSubAgents: ps?.CanCreateSubAgents ?? false,
            CanCreateContainers: ps?.CanCreateContainers ?? false,
            CanRegisterInfoStores: ps?.CanRegisterInfoStores ?? false,
            CanAccessLocalhostInBrowser: ps?.CanAccessLocalhostInBrowser ?? false,
            CanAccessLocalhostCli: ps?.CanAccessLocalhostCli ?? false,
            DangerousShellAccesses: MapGrants(ps?.DangerousShellAccesses, a => a.SystemUserId, a => a.Clearance),
            SafeShellAccesses: MapGrants(ps?.SafeShellAccesses, a => a.ContainerId, a => a.Clearance),
            ContainerAccesses: MapGrants(ps?.ContainerAccesses, a => a.ContainerId, a => a.Clearance),
            WebsiteAccesses: MapGrants(ps?.WebsiteAccesses, a => a.WebsiteId, a => a.Clearance),
            SearchEngineAccesses: MapGrants(ps?.SearchEngineAccesses, a => a.SearchEngineId, a => a.Clearance),
            LocalInfoStoreAccesses: MapGrants(ps?.LocalInfoStorePermissions, a => a.LocalInformationStoreId, a => a.Clearance),
            ExternalInfoStoreAccesses: MapGrants(ps?.ExternalInfoStorePermissions, a => a.ExternalInformationStoreId, a => a.Clearance),
            AudioDeviceAccesses: MapGrants(ps?.AudioDeviceAccesses, a => a.AudioDeviceId, a => a.Clearance),
            AgentAccesses: MapGrants(ps?.AgentPermissions, a => a.AgentId, a => a.Clearance),
            TaskAccesses: MapGrants(ps?.TaskPermissions, a => a.ScheduledTaskId, a => a.Clearance),
            SkillAccesses: MapGrants(ps?.SkillPermissions, a => a.SkillId, a => a.Clearance));

    private static IReadOnlyList<ResourceGrant> MapGrants<T>(
        ICollection<T>? accesses,
        Func<T, Guid> resourceSelector,
        Func<T, PermissionClearance> clearanceSelector)
    {
        if (accesses is null or { Count: 0 })
            return [];

        return accesses
            .Select(a => new ResourceGrant(resourceSelector(a), clearanceSelector(a)))
            .ToList();
    }
}
