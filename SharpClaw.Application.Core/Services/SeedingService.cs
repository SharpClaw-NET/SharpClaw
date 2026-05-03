using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

/// <summary>
/// Seeds the admin role and initial admin user on startup when they do not already exist.
/// Reads credentials from the <c>Admin:Username</c> and <c>Admin:Password</c> configuration keys.
/// </summary>
public sealed class SeedingService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SeedingService> logger,
    ModuleRegistry moduleRegistry) : IHostedLifecycleService
{
    private const string AdminRoleName = "Admin";

    public Task StartingAsync(CancellationToken cancellationToken) => SeedAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var clientFactory = scope.ServiceProvider.GetRequiredService<ProviderApiClientFactory>();

        var adminRole = await SeedAdminRoleAsync(db, ct);
        await SeedAdminUserAsync(db, adminRole, ct);
        await SeedWellKnownProvidersAsync(db, clientFactory, ct);
    }

    private async Task<RoleDB> SeedAdminRoleAsync(SharpClawDbContext db, CancellationToken ct)
    {
        var existing = await db.Roles
            .Include(r => r.PermissionSet)
            .FirstOrDefaultAsync(r => r.Name == AdminRoleName, ct);
        if (existing is not null)
        {
            if (existing.PermissionSetId is null)
            {
                // No permission set at all — create the full set.
                var ps = CreateAdminPermissions();
                db.PermissionSets.Add(ps);
                await db.SaveChangesAsync(ct);
                existing.PermissionSetId = ps.Id;
                await db.SaveChangesAsync(ct);
            }
            else if (configuration.GetValue<bool>("Admin:ReconcilePermissions"))
            {
                // Reconcile: bring the existing permission set up to date
                // in case new flags or grant types were added after initial seeding.
                // Off by default — enable via Admin:ReconcilePermissions = true in .env.
                await ReconcileAdminPermissionsAsync(db, existing.PermissionSetId.Value, ct);
            }
            return existing;
        }

        logger.LogInformation("Seeding '{Role}' role.", AdminRoleName);

        var permissionSet = CreateAdminPermissions();
        db.PermissionSets.Add(permissionSet);
        await db.SaveChangesAsync(ct);

        var role = new RoleDB { Name = AdminRoleName, PermissionSetId = permissionSet.Id };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);

        return role;
    }

    private PermissionSetDB CreateAdminPermissions() => new()
    {
        // Global flags — all registered flag keys enabled for admin
        // with explicit Independent clearance on each flag.
        GlobalFlags = [.. moduleRegistry.GetAllRegisteredGlobalFlags()
            .Select(fk => new GlobalFlagDB
            {
                FlagKey = fk,
                Clearance = PermissionClearance.Independent,
            })],

        // Wildcard grants — access to ALL resources of each type.
        // WellKnownIds.AllResources is recognised as a universal match
        // by AgentActionService and is immutable at runtime.
        //
        // IMPORTANT: Clearance MUST be set to Independent. ResourceAccessDB
        // defaults Clearance to Unset, and EvaluateResourceAccessAsync treats
        // Unset as "grant is inert, deny." Without this the Admin role has
        // wildcard rows that look right in the DB but always deny at runtime.
        // Previously shipped without this line, causing every agent tool call
        // against a resource type to be denied despite the role's wildcard.
        ResourceAccesses = [.. moduleRegistry.GetAllRegisteredResourceTypes()
            .Select(rt => new ResourceAccessDB
            {
                ResourceType = rt,
                ResourceId = WellKnownIds.AllResources,
                Clearance = PermissionClearance.Independent,
            })],
    };

    /// <summary>
    /// Ensures an existing admin permission set has all current flags enabled
    /// and all wildcard resource grants present. Runs on every startup so newly
    /// added permission types are automatically back-filled.
    /// </summary>
    private async Task ReconcileAdminPermissionsAsync(
        SharpClawDbContext db, Guid psId, CancellationToken ct)
    {
        var ps = await db.PermissionSets
            .Include(p => p.ResourceAccesses)
            .Include(p => p.GlobalFlags)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == psId, ct);

        if (ps is null)
            return;

        var changed = false;

        // ── Global flag grants ─────────────────────────────────────
        var allFlagKeys = moduleRegistry.GetAllRegisteredGlobalFlags();
        foreach (var flagKey in allFlagKeys)
        {
            var existing = ps.GlobalFlags.FirstOrDefault(f => f.FlagKey == flagKey);
            if (existing is null)
            {
                ps.GlobalFlags.Add(new GlobalFlagDB
                {
                    PermissionSetId = psId,
                    FlagKey = flagKey,
                    Clearance = PermissionClearance.Independent,
                });
                changed = true;
            }
            else if (existing.Clearance != PermissionClearance.Independent)
            {
                existing.Clearance = PermissionClearance.Independent;
                changed = true;
            }
        }

        // ── Wildcard resource grants ──────────────────────────────
        var allResourceTypes = moduleRegistry.GetAllRegisteredResourceTypes();

        foreach (var rt in allResourceTypes)
        {
            // Two sub-tasks, matching the GlobalFlags loop above:
            //   1. Add a wildcard row for any module-registered ResourceType
            //      the admin PS doesn't have yet.
            //   2. Upgrade any existing wildcard row whose Clearance is not
            //      Independent (e.g. Unset, left over from the shipped-broken
            //      seed that forgot to set Clearance on wildcard grants) so
            //      the grant is actually effective rather than inert.
            var existingAccess = ps.ResourceAccesses.FirstOrDefault(a =>
                a.ResourceType == rt && a.ResourceId == WellKnownIds.AllResources);

            if (existingAccess is null)
            {
                ps.ResourceAccesses.Add(new ResourceAccessDB
                {
                    PermissionSetId = psId,
                    ResourceType = rt,
                    ResourceId = WellKnownIds.AllResources,
                    Clearance = PermissionClearance.Independent,
                });
                changed = true;
            }
            else if (existingAccess.Clearance != PermissionClearance.Independent)
            {
                existingAccess.Clearance = PermissionClearance.Independent;
                changed = true;
            }
        }

        if (changed)
        {
            logger.LogInformation("Reconciled admin permissions — added missing grants.");
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SeedAdminUserAsync(SharpClawDbContext db, RoleDB adminRole, CancellationToken ct)
    {
        var hasAdmin = await db.Users.AnyAsync(u => u.RoleId == adminRole.Id, ct);
        if (hasAdmin)
            return;

        var username = configuration["Admin:Username"];
        var password = configuration["Admin:Password"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "No admin user exists and Admin:Username / Admin:Password are not configured. Skipping admin seed.");
            return;
        }

        logger.LogInformation("Seeding admin user '{Username}'.", username);

        var salt = PasswordHelper.GenerateSalt();
        var hash = PasswordHelper.Hash(password, salt);

        var user = new UserDB
        {
            Username = username,
            PasswordHash = hash,
            PasswordSalt = salt,
            RoleId = adminRole.Id,
            IsUserAdmin = true
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedWellKnownProvidersAsync(
        SharpClawDbContext db, ProviderApiClientFactory clientFactory, CancellationToken ct)
    {
        var existing = await db.Providers
            .Select(p => p.ProviderKey)
            .ToHashSetAsync(ct);

        // Plugins drive the seed list — disabling a provider module simply
        // means its IProviderPlugin is no longer registered, so it won't be
        // seeded. The previous hardcoded key list is gone in Phase 5.
        // Custom is excluded: it requires an operator-supplied endpoint and
        // is created on demand via the providers/add CLI/API.
        var seedablePlugins = clientFactory.Plugins
            .Where(p => p.ProviderKey != WellKnownProviderKeys.Custom
                     && !existing.Contains(p.ProviderKey))
            .ToList();

        if (seedablePlugins.Count == 0)
            return;

        logger.LogInformation("Seeding {Count} well-known provider(s): {Types}.",
            seedablePlugins.Count,
            string.Join(", ", seedablePlugins.Select(p => p.ProviderKey)));

        foreach (var plugin in seedablePlugins)
        {
            db.Providers.Add(new ProviderDB
            {
                Name = plugin.DisplayName,
                ProviderKey = plugin.ProviderKey
            });
        }

        await db.SaveChangesAsync(ct);
    }
}

