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

        // Replace the permission set (delete old access entries, rebuild).
        PermissionSetDB ps;
        if (role.PermissionSetId is { } existingPsId)
        {
            ps = await LoadFullPermissionSetAsync(existingPsId, ct)
                ?? throw new InvalidOperationException(
                    $"Permission set {existingPsId} not found.");

            // Clear existing grant collections.
            ps.ResourceAccesses.Clear();
            ps.GlobalFlags.Clear();
        }
        else
        {
            ps = new PermissionSetDB();
            db.PermissionSets.Add(ps);
            await db.SaveChangesAsync(ct);
            role.PermissionSetId = ps.Id;
        }

        // Apply global flags — generic loop over dictionary.
        ps.DefaultClearance = request.DefaultClearance;
        if (request.GlobalFlags is not null)
        {
            foreach (var (key, clearance) in request.GlobalFlags)
            {
                ps.GlobalFlags.Add(new GlobalFlagDB
                {
                    FlagKey = key,
                    Clearance = clearance,
                });
            }
        }

        // Apply per-resource grants — generic loop over dictionary.
        if (request.ResourceGrants is not null)
        {
            foreach (var (resourceType, grants) in request.ResourceGrants)
            {
                foreach (var g in grants)
                {
                    ps.ResourceAccesses.Add(new ResourceAccessDB
                    {
                        ResourceType = resourceType,
                        ResourceId = g.ResourceId,
                        Clearance = g.Clearance,
                    });
                }
            }
        }

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
            DefaultClearance: ps?.DefaultClearance ?? PermissionClearance.Unset,
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
        var exists = await db.Roles.AnyAsync(
            r => r.Name == name && (excludeId == null || r.Id != excludeId), ct);
        if (exists)
            throw new InvalidOperationException($"A role named '{name}' already exists.");
    }
}
