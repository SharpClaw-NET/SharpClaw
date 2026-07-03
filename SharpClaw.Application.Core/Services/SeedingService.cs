using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;
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
    ModuleRegistry moduleRegistry,
    AdminPermissionSeedEngine adminPermissions) : IHostedLifecycleService
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
                // No permission set at all - create the full set.
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
                // Off by default - enable via Admin:ReconcilePermissions = true in .env.
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

    private PermissionSetDB CreateAdminPermissions()
    {
        var plan = adminPermissions.BuildCreatePlan(
            moduleRegistry.GetAllRegisteredGlobalFlags(),
            moduleRegistry.GetAllRegisteredResourceTypes());

        return new PermissionSetDB
        {
            // Global flags - all registered flag keys enabled for admin
            // with explicit Independent clearance on each flag.
            GlobalFlags = [.. plan.GlobalFlags.Select(grant => new GlobalFlagDB
            {
                FlagKey = grant.FlagKey,
                Clearance = grant.Clearance,
            })],

            // Wildcard grants - access to ALL resources of each type.
            // WellKnownIds.AllResources is recognised as a universal match
            // by AgentActionService and is immutable at runtime.
            //
            // Clearance must be set to Independent. ResourceAccessDB defaults
            // Clearance to Unset, and EvaluateResourceAccessAsync treats Unset
            // as "grant is inert, deny."
            ResourceAccesses = [.. plan.WildcardResources.Select(grant =>
                new ResourceAccessDB
                {
                    ResourceType = grant.ResourceType,
                    ResourceId = WellKnownIds.AllResources,
                    Clearance = grant.Clearance,
                })],
        };
    }

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

        var plan = adminPermissions.BuildReconcilePlan(
            moduleRegistry.GetAllRegisteredGlobalFlags(),
            moduleRegistry.GetAllRegisteredResourceTypes(),
            ps.GlobalFlags
                .Select(flag => new AdminGlobalFlagGrantFact(
                    flag.FlagKey,
                    flag.Clearance))
                .ToList(),
            ps.ResourceAccesses
                .Where(access => access.ResourceId == WellKnownIds.AllResources)
                .Select(access => new AdminWildcardResourceGrantFact(
                    access.ResourceType,
                    access.Clearance))
                .ToList());

        foreach (var grant in plan.MissingGlobalFlags)
        {
            ps.GlobalFlags.Add(new GlobalFlagDB
            {
                PermissionSetId = psId,
                FlagKey = grant.FlagKey,
                Clearance = grant.Clearance,
            });
        }

        foreach (var update in plan.GlobalFlagUpdates)
        {
            var existing = ps.GlobalFlags.First(flag =>
                flag.FlagKey == update.FlagKey);
            existing.Clearance = update.Clearance;
        }

        foreach (var grant in plan.MissingWildcardResources)
        {
            ps.ResourceAccesses.Add(new ResourceAccessDB
            {
                PermissionSetId = psId,
                ResourceType = grant.ResourceType,
                ResourceId = WellKnownIds.AllResources,
                Clearance = grant.Clearance,
            });
        }

        foreach (var update in plan.WildcardResourceUpdates)
        {
            var existing = ps.ResourceAccesses.First(access =>
                access.ResourceType == update.ResourceType
                && access.ResourceId == WellKnownIds.AllResources);
            existing.Clearance = update.Clearance;
        }

        if (plan.HasChanges)
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

        // Plugins drive the seed list - disabling a provider module simply
        // means its IProviderPlugin is no longer registered, so it won't be
        // seeded. Plugins that opt out of seeding (e.g. Custom, which needs
        // an operator-supplied endpoint) set IsSeedable=false.
        var seedablePlugins = clientFactory.Plugins
            .Where(p => p.IsSeedable
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
