using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
public sealed class RoleService(SharpClawDbContext db, IConfiguration configuration)
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

    /// <summary>
    /// Creates a new role with an empty permission set.
    /// </summary>
    public async Task<RoleResponse> CreateAsync(
        string name, CancellationToken ct = default)
    {
        if (IsUniqueRoleNamesEnforced())
            await EnsureRoleNameUniqueAsync(name, excludeId: null, ct);

        var ps = new PermissionSetDB();
        db.PermissionSets.Add(ps);
        await db.SaveChangesAsync(ct);

        var role = new RoleDB { Name = name, PermissionSetId = ps.Id };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);

        return new RoleResponse(role.Id, role.Name, role.PermissionSetId);
    }

    public async Task<RoleResponse?> GetByIdAsync(
        Guid roleId, CancellationToken ct = default)
    {
        return await db.Roles
            .Where(r => r.Id == roleId)
            .Select(r => new RoleResponse(r.Id, r.Name, r.PermissionSetId))
            .FirstOrDefaultAsync(ct);
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

        // Admin users bypass permission validation — they can grant anything.
        var isAdmin = await db.Users.AnyAsync(u => u.Id == callerUserId && u.IsUserAdmin, ct);
        if (!isAdmin)
        {
            // Load caller's own permission set for validation.
            var callerPs = await LoadCallerPermissionSetAsync(callerUserId, ct);

            ValidateGlobalFlags(request, callerPs);
            ValidateResourceGrants(request, callerPs);
        }

        // Reconcile the permission set via incremental diff — never deletes wildcard grants.
        PermissionSetDB ps;
        if (role.PermissionSetId is { } existingPsId)
        {
            ps = await LoadFullPermissionSetAsync(existingPsId, ct)
                ?? throw new InvalidOperationException(
                    $"Permission set {existingPsId} not found.");
        }
        else
        {
            ps = new PermissionSetDB();
            db.PermissionSets.Add(ps);
            await db.SaveChangesAsync(ct);
            role.PermissionSetId = ps.Id;
        }

        ReconcileGlobalFlags(ps, request.GlobalFlags);
        ReconcileResourceAccesses(ps, request.ResourceGrants);

        await db.SaveChangesAsync(ct);

        return ToResponse(role, ps);
    }

    // ═══════════════════════════════════════════════════════════════
    // Validation — you can only grant what you hold
    // ═══════════════════════════════════════════════════════════════

    private static void ValidateGlobalFlags(
        SetRolePermissionsRequest request, PermissionSetDB? callerPs)
    {
        if (request.GlobalFlags is null or { Count: 0 })
            return;

        if (callerPs is null)
            throw new UnauthorizedAccessException(
                "You have no permissions — cannot grant any global flags.");

        foreach (var (flagKey, _) in request.GlobalFlags)
        {
            if (!callerPs.GlobalFlags.Any(f => f.FlagKey == flagKey))
                throw new UnauthorizedAccessException(
                    $"Cannot grant {flagKey} — you do not hold this permission.");
        }
    }

    private static void ValidateResourceGrants(
        SetRolePermissionsRequest request, PermissionSetDB? callerPs)
    {
        if (request.ResourceGrants is null or { Count: 0 })
            return;

        foreach (var (resourceType, grants) in request.ResourceGrants)
            ValidateGrants(resourceType, resourceType, grants, callerPs);
    }

    /// <summary>
    /// For each requested grant, the caller must hold either the exact
    /// resource ID or the <see cref="WellKnownIds.AllResources"/> wildcard.
    /// </summary>
    private static void ValidateGrants(
        string name,
        string resourceType,
        IReadOnlyList<ResourceGrant>? requested,
        PermissionSetDB? callerPs)
    {
        if (requested is null or { Count: 0 })
            return;

        var callerGrants = callerPs?.ResourceAccesses
            .Where(a => a.ResourceType == resourceType)
            .Select(a => a.ResourceId)
            .ToHashSet();

        if (callerGrants is null or { Count: 0 })
            throw new UnauthorizedAccessException(
                $"Cannot grant {name} — you hold no grants of this type.");

        var hasWildcard = callerGrants.Contains(WellKnownIds.AllResources);

        foreach (var grant in requested)
        {
            if (hasWildcard)
                continue;

            if (!callerGrants.Contains(grant.ResourceId))
                throw new UnauthorizedAccessException(
                    $"Cannot grant {name} for resource {grant.ResourceId} " +
                    "— you do not hold this permission.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

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
            .Include(p => p.GlobalFlags)
            .Include(p => p.ResourceAccesses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == psId, ct);
    }

    private static RolePermissionsResponse ToResponse(RoleDB role, PermissionSetDB? ps) =>
        new(
            RoleId: role.Id,
            RoleName: role.Name,
            GlobalFlags: ps?.GlobalFlags
                .ToDictionary(f => f.FlagKey, f => f.Clearance)
                ?? new Dictionary<string, PermissionClearance>(),
            ResourceGrants: ps is null
                ? new Dictionary<string, IReadOnlyList<ResourceGrant>>()
                : ps.ResourceAccesses
                    .GroupBy(a => a.ResourceType)
                    .ToDictionary(
                        g => g.Key,
                        g => (IReadOnlyList<ResourceGrant>)g
                            .Select(a => new ResourceGrant(a.ResourceId, a.Clearance))
                            .ToList()));

    private bool IsUniqueRoleNamesEnforced()
    {
        var value = configuration["UniqueNames:Roles"];
        return value is null || !bool.TryParse(value, out var enforced) || enforced;
    }

    private async Task EnsureRoleNameUniqueAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var normalized = name.Trim();
        var names = await db.Roles
            .Where(r => excludeId == null || r.Id != excludeId)
            .Select(r => r.Name)
            .ToListAsync(ct);
        if (names.Any(n => n.Trim().Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A role named '{name}' already exists.");
    }

    /// <summary>
    /// Reconciles global flags in place: adds new flags, updates changed clearances,
    /// removes absent flags. Global flags carry no wildcard semantics.
    /// </summary>
    private static void ReconcileGlobalFlags(
        PermissionSetDB ps,
        IReadOnlyDictionary<string, PermissionClearance>? requested)
    {
        var requestedMap = requested ?? new Dictionary<string, PermissionClearance>();

        foreach (var existing in ps.GlobalFlags.ToList())
        {
            if (!requestedMap.TryGetValue(existing.FlagKey, out var newClearance))
                ps.GlobalFlags.Remove(existing);
            else if (existing.Clearance != newClearance)
                existing.Clearance = newClearance;
            // else: unchanged — leave untouched, no EF state change.
        }

        var existingKeys = ps.GlobalFlags.Select(f => f.FlagKey).ToHashSet();
        foreach (var (key, clearance) in requestedMap)
            if (!existingKeys.Contains(key))
                ps.GlobalFlags.Add(new GlobalFlagDB { FlagKey = key, Clearance = clearance });
    }

    /// <summary>
    /// Reconciles resource-access grants in place.
    /// Wildcard grants (<see cref="WellKnownIds.AllResources"/>) are preserved and
    /// cannot be removed or have their clearance changed through this endpoint.
    /// Non-wildcard grants may be freely added, updated, or removed.
    /// </summary>
    private static void ReconcileResourceAccesses(
        PermissionSetDB ps,
        IReadOnlyDictionary<string, IReadOnlyList<ResourceGrant>>? requested)
    {
        // Build a flat lookup of the requested grants.
        var requestedMap = requested?
            .SelectMany(kvp => kvp.Value.Select(g => (ResourceType: kvp.Key, g.ResourceId, g.Clearance)))
            .ToDictionary(t => (t.ResourceType, t.ResourceId), t => t.Clearance)
            ?? [];

        // Pass 1: handle existing rows — remove absent ones, update changed clearances.
        foreach (var access in ps.ResourceAccesses.ToList())
        {
            var key = (access.ResourceType, access.ResourceId);
            if (!requestedMap.TryGetValue(key, out var newClearance))
            {
                if (access.ResourceId == WellKnownIds.AllResources)
                    throw new InvalidOperationException(
                        $"Wildcard grant for '{access.ResourceType}' is immutable and cannot be removed.");

                ps.ResourceAccesses.Remove(access);
            }
            else if (access.Clearance != newClearance)
            {
                if (access.ResourceId == WellKnownIds.AllResources)
                    throw new InvalidOperationException(
                        $"Clearance of wildcard grant for '{access.ResourceType}' is immutable and cannot be changed.");

                access.Clearance = newClearance;
            }
            // else: unchanged — leave the row untouched, no EF state change.
        }

        // Pass 2: add rows that are in the request but not yet in the set.
        var existingKeys = ps.ResourceAccesses
            .Select(a => (a.ResourceType, a.ResourceId))
            .ToHashSet();

        foreach (var (resourceType, grants) in requested ?? new Dictionary<string, IReadOnlyList<ResourceGrant>>())
            foreach (var grant in grants)
                if (!existingKeys.Contains((resourceType, grant.ResourceId)))
                    ps.ResourceAccesses.Add(new ResourceAccessDB
                    {
                        ResourceType = resourceType,
                        ResourceId = grant.ResourceId,
                        Clearance = grant.Clearance,
                    });
    }
}
