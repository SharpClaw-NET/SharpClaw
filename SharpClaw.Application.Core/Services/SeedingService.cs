using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    ILogger<SeedingService> logger) : IHostedLifecycleService
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
        await SeedDefaultAudioDeviceAsync(db, ct);
        await SeedDefaultDisplayDeviceAsync(db, ct);
        await ReconcileStaleTranscriptionJobsAsync(db, ct);
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

    private static PermissionSetDB CreateAdminPermissions() => new()
    {
        DefaultClearance = PermissionClearance.Independent,
        CanCreateSubAgents = true,
        CanCreateContainers = true,
        CanRegisterInfoStores = true,
        CanAccessLocalhostInBrowser = true,
        CanAccessLocalhostCli = true,
        CanClickDesktop = true,
        CanTypeOnDesktop = true,
        CanReadCrossThreadHistory = true,
        CanEditAgentHeader = true,
        CanEditChannelHeader = true,

        // Wildcard grants — access to ALL resources of each type.
        // WellKnownIds.AllResources is recognised as a universal match
        // by AgentActionService and is immutable at runtime.
        DangerousShellAccesses       = [new() { SystemUserId               = WellKnownIds.AllResources }],
        SafeShellAccesses            = [new() { ContainerId               = WellKnownIds.AllResources }],
        LocalInfoStorePermissions    = [new() { LocalInformationStoreId    = WellKnownIds.AllResources }],
        ExternalInfoStorePermissions = [new() { ExternalInformationStoreId = WellKnownIds.AllResources }],
        WebsiteAccesses              = [new() { WebsiteId                  = WellKnownIds.AllResources }],
        SearchEngineAccesses         = [new() { SearchEngineId             = WellKnownIds.AllResources }],
        ContainerAccesses            = [new() { ContainerId                = WellKnownIds.AllResources }],
        AudioDeviceAccesses          = [new() { AudioDeviceId              = WellKnownIds.AllResources }],
        DisplayDeviceAccesses        = [new() { DisplayDeviceId            = WellKnownIds.AllResources }],
        EditorSessionAccesses        = [new() { EditorSessionId            = WellKnownIds.AllResources }],
        AgentPermissions             = [new() { AgentId                    = WellKnownIds.AllResources }],
        TaskPermissions              = [new() { ScheduledTaskId            = WellKnownIds.AllResources }],
        SkillPermissions             = [new() { SkillId                    = WellKnownIds.AllResources }],
        AgentHeaderAccesses          = [new() { AgentId                    = WellKnownIds.AllResources }],
        ChannelHeaderAccesses        = [new() { ChannelId                  = WellKnownIds.AllResources }],
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
            .Include(p => p.DangerousShellAccesses)
            .Include(p => p.SafeShellAccesses)
            .Include(p => p.ContainerAccesses)
            .Include(p => p.WebsiteAccesses)
            .Include(p => p.SearchEngineAccesses)
            .Include(p => p.LocalInfoStorePermissions)
            .Include(p => p.ExternalInfoStorePermissions)
            .Include(p => p.AudioDeviceAccesses)
            .Include(p => p.DisplayDeviceAccesses)
            .Include(p => p.EditorSessionAccesses)
            .Include(p => p.AgentPermissions)
            .Include(p => p.TaskPermissions)
            .Include(p => p.SkillPermissions)
            .Include(p => p.AgentHeaderAccesses)
            .Include(p => p.ChannelHeaderAccesses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == psId, ct);

        if (ps is null)
            return;

        var changed = false;

        // ── Global flags ──────────────────────────────────────────
        if (ps.DefaultClearance != PermissionClearance.Independent)
        {
            ps.DefaultClearance = PermissionClearance.Independent;
            changed = true;
        }
        changed |= ReconcileFlag(v => ps.CanCreateSubAgents = v, ps.CanCreateSubAgents);
        changed |= ReconcileFlag(v => ps.CanCreateContainers = v, ps.CanCreateContainers);
        changed |= ReconcileFlag(v => ps.CanRegisterInfoStores = v, ps.CanRegisterInfoStores);
        changed |= ReconcileFlag(v => ps.CanAccessLocalhostInBrowser = v, ps.CanAccessLocalhostInBrowser);
        changed |= ReconcileFlag(v => ps.CanAccessLocalhostCli = v, ps.CanAccessLocalhostCli);
        changed |= ReconcileFlag(v => ps.CanClickDesktop = v, ps.CanClickDesktop);
        changed |= ReconcileFlag(v => ps.CanTypeOnDesktop = v, ps.CanTypeOnDesktop);
        changed |= ReconcileFlag(v => ps.CanReadCrossThreadHistory = v, ps.CanReadCrossThreadHistory);
        changed |= ReconcileFlag(v => ps.CanEditAgentHeader = v, ps.CanEditAgentHeader);
        changed |= ReconcileFlag(v => ps.CanEditChannelHeader = v, ps.CanEditChannelHeader);

        // ── Wildcard resource grants ──────────────────────────────
        changed |= EnsureWildcard(ps.DangerousShellAccesses, a => a.SystemUserId,
            () => new DangerousShellAccessDB { PermissionSetId = psId, SystemUserId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.SafeShellAccesses, a => a.ContainerId,
            () => new SafeShellAccessDB { PermissionSetId = psId, ContainerId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.ContainerAccesses, a => a.ContainerId,
            () => new ContainerAccessDB { PermissionSetId = psId, ContainerId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.WebsiteAccesses, a => a.WebsiteId,
            () => new WebsiteAccessDB { PermissionSetId = psId, WebsiteId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.SearchEngineAccesses, a => a.SearchEngineId,
            () => new SearchEngineAccessDB { PermissionSetId = psId, SearchEngineId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.LocalInfoStorePermissions, a => a.LocalInformationStoreId,
            () => new LocalInfoStoreAccessDB { PermissionSetId = psId, LocalInformationStoreId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.ExternalInfoStorePermissions, a => a.ExternalInformationStoreId,
            () => new ExternalInfoStoreAccessDB { PermissionSetId = psId, ExternalInformationStoreId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.AudioDeviceAccesses, a => a.AudioDeviceId,
            () => new AudioDeviceAccessDB { PermissionSetId = psId, AudioDeviceId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.DisplayDeviceAccesses, a => a.DisplayDeviceId,
            () => new DisplayDeviceAccessDB { PermissionSetId = psId, DisplayDeviceId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.EditorSessionAccesses, a => a.EditorSessionId,
            () => new EditorSessionAccessDB { PermissionSetId = psId, EditorSessionId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.AgentPermissions, a => a.AgentId,
            () => new AgentManagementAccessDB { PermissionSetId = psId, AgentId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.TaskPermissions, a => a.ScheduledTaskId,
            () => new TaskManageAccessDB { PermissionSetId = psId, ScheduledTaskId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.SkillPermissions, a => a.SkillId,
            () => new SkillManageAccessDB { PermissionSetId = psId, SkillId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.AgentHeaderAccesses, a => a.AgentId,
            () => new AgentHeaderAccessDB { PermissionSetId = psId, AgentId = WellKnownIds.AllResources });
        changed |= EnsureWildcard(ps.ChannelHeaderAccesses, a => a.ChannelId,
            () => new ChannelHeaderAccessDB { PermissionSetId = psId, ChannelId = WellKnownIds.AllResources });

        if (changed)
        {
            logger.LogInformation("Reconciled admin permissions — added missing grants.");
            await db.SaveChangesAsync(ct);
        }

        return;

        // Local helpers ────────────────────────────────────────────
        static bool ReconcileFlag(Action<bool> setter, bool current)
        {
            if (current) return false;
            setter(true);
            return true;
        }

        static bool EnsureWildcard<T>(
            ICollection<T> collection, Func<T, Guid> selector, Func<T> factory)
        {
            if (collection.Any(a => selector(a) == WellKnownIds.AllResources))
                return false;
            collection.Add(factory());
            return true;
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
                Name = pt.ToString(),
                ProviderType = pt
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedDefaultAudioDeviceAsync(SharpClawDbContext db, CancellationToken ct)
    {
        var exists = await db.AudioDevices
            .AnyAsync(d => d.DeviceIdentifier == "default", ct);
        if (exists)
            return;

        logger.LogInformation("Seeding default audio device.");

        db.AudioDevices.Add(new AudioDeviceDB
        {
            Name = "Default",
            DeviceIdentifier = "default",
            Description = "System default audio input device"
        });

        await db.SaveChangesAsync(ct);
    }

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
    /// Marks transcription jobs left in <see cref="AgentJobStatus.Executing"/> from a previous
    /// session as <see cref="AgentJobStatus.Cancelled"/>. No orchestrator loops survive a restart,
    /// so these are guaranteed stale.
    /// </summary>
    private async Task ReconcileStaleTranscriptionJobsAsync(SharpClawDbContext db, CancellationToken ct)
    {
        var staleJobs = await db.AgentJobs
            .Where(j => (j.ActionType == AgentActionType.TranscribeFromAudioDevice
                      || j.ActionType == AgentActionType.TranscribeFromAudioStream
                      || j.ActionType == AgentActionType.TranscribeFromAudioFile)
                && (j.Status == AgentJobStatus.Executing || j.Status == AgentJobStatus.Queued))
            .ToListAsync(ct);

        if (staleJobs.Count == 0)
            return;

        logger.LogInformation("Reconciling {Count} stale transcription job(s) from previous session.", staleJobs.Count);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in staleJobs)
        {
            job.Status = AgentJobStatus.Cancelled;
            job.CompletedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
