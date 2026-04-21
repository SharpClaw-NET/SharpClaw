using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
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

        var adminRole = await SeedAdminRoleAsync(db, ct);
        await SeedAdminUserAsync(db, adminRole, ct);
        await SeedWellKnownProvidersAsync(db, ct);
        await SeedDefaultDisplayDeviceAsync(db, ct);
        await SeedWellKnownSearchEnginesAsync(db, ct);
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
        ResourceAccesses = [.. moduleRegistry.GetAllRegisteredResourceTypes()
            .Select(rt => new ResourceAccessDB
            {
                ResourceType = rt,
                ResourceId = WellKnownIds.AllResources,
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
            if (!ps.ResourceAccesses.Any(a =>
                    a.ResourceType == rt && a.ResourceId == WellKnownIds.AllResources))
            {
                ps.ResourceAccesses.Add(new ResourceAccessDB
                {
                    PermissionSetId = psId,
                    ResourceType = rt,
                    ResourceId = WellKnownIds.AllResources,
                });
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

    private async Task SeedWellKnownProvidersAsync(SharpClawDbContext db, CancellationToken ct)
    {
        var existing = await db.Providers
            .Select(p => p.ProviderType)
            .ToHashSetAsync(ct);

        var toSeed = Enum.GetValues<ProviderType>()
            .Where(pt => pt != ProviderType.Custom && !existing.Contains(pt))
            .ToList();

        if (toSeed.Count == 0)
            return;

        logger.LogInformation("Seeding {Count} well-known provider(s): {Types}.",
            toSeed.Count, string.Join(", ", toSeed));

        foreach (var pt in toSeed)
        {
            db.Providers.Add(new ProviderDB
            {
                Name = DisplayNameFor(pt),
                ProviderType = pt
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static string DisplayNameFor(ProviderType pt) => pt switch
    {
        ProviderType.LlamaSharp => "LlamaSharp (Local)",
        ProviderType.Whisper    => "Whisper (Local)",
        _                       => pt.ToString(),
    };

    private async Task SeedDefaultDisplayDeviceAsync(SharpClawDbContext db, CancellationToken ct)
    {
        var exists = await db.DisplayDevices
            .AnyAsync(d => d.DeviceIdentifier == "display-0", ct);
        if (exists)
            return;

        logger.LogInformation("Seeding default display device.");

        db.DisplayDevices.Add(new DisplayDeviceDB
        {
            Name = "Primary Display",
            DeviceIdentifier = "display-0",
            DisplayIndex = 0,
            Description = "System primary display"
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Seeds one <see cref="SearchEngineDB"/> entry per well-known
    /// <see cref="SearchEngineType"/> (excluding <c>Custom</c>).
    /// API keys are left empty — users supply them after first run.
    /// </summary>
    private async Task SeedWellKnownSearchEnginesAsync(SharpClawDbContext db, CancellationToken ct)
    {
        var existing = await db.SearchEngines
            .Select(e => e.Type)
            .ToHashSetAsync(ct);

        var defaultEndpoints = new Dictionary<SearchEngineType, string>
        {
            [SearchEngineType.Google] = "https://www.googleapis.com/customsearch/v1",
            [SearchEngineType.Bing] = "https://api.bing.microsoft.com/v7.0/search",
            [SearchEngineType.DuckDuckGo] = "https://api.duckduckgo.com/",
            [SearchEngineType.Brave] = "https://api.search.brave.com/res/v1/web/search",
            [SearchEngineType.SearXNG] = "https://searx.be/search",
            [SearchEngineType.Tavily] = "https://api.tavily.com/search",
            [SearchEngineType.Serper] = "https://google.serper.dev/search",
            [SearchEngineType.Kagi] = "https://kagi.com/api/v0/search",
            [SearchEngineType.YouDotCom] = "https://api.ydc-index.io/search",
            [SearchEngineType.Mojeek] = "https://www.mojeek.com/search",
            [SearchEngineType.Yandex] = "https://yandex.com/search/xml",
            [SearchEngineType.Baidu] = "https://api.baidu.com/search",
        };

        var toSeed = Enum.GetValues<SearchEngineType>()
            .Where(t => t != SearchEngineType.Custom && !existing.Contains(t))
            .ToList();

        if (toSeed.Count == 0)
            return;

        logger.LogInformation("Seeding {Count} well-known search engine(s): {Types}.",
            toSeed.Count, string.Join(", ", toSeed));

        foreach (var type in toSeed)
        {
            db.SearchEngines.Add(new SearchEngineDB
            {
                Name = type.ToString(),
                Type = type,
                Endpoint = defaultEndpoints.GetValueOrDefault(type, string.Empty),
            });
        }

        await db.SaveChangesAsync(ct);
    }

}
