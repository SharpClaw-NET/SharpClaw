using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        await ReconcileStaleTranscriptionJobsAsync(db, ct);
    }

    private async Task<RoleDB> SeedAdminRoleAsync(SharpClawDbContext db, CancellationToken ct)
    {
        var existing = await db.Roles
            .Include(r => r.PermissionSet)
            .FirstOrDefaultAsync(r => r.Name == AdminRoleName, ct);
        if (existing is not null)
        {
            // Ensure the permissions record exists for pre-existing roles
            if (existing.PermissionSetId is null)
            {
                var ps = CreateAdminPermissions();
                db.PermissionSets.Add(ps);
                await db.SaveChangesAsync(ct);
                existing.PermissionSetId = ps.Id;
                await db.SaveChangesAsync(ct);
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

        // Wildcard grants — access to ALL resources of each type.
        // WellKnownIds.AllResources is recognised as a universal match
        // by AgentActionService and is immutable at runtime.
        DangerousShellAccesses      = [new() { SystemUserId              = WellKnownIds.AllResources }],
        SafeShellAccesses           = [new() { ContainerId              = WellKnownIds.AllResources }],
        LocalInfoStorePermissions   = [new() { LocalInformationStoreId   = WellKnownIds.AllResources }],
        ExternalInfoStorePermissions = [new() { ExternalInformationStoreId = WellKnownIds.AllResources }],
        WebsiteAccesses             = [new() { WebsiteId                 = WellKnownIds.AllResources }],
        SearchEngineAccesses        = [new() { SearchEngineId            = WellKnownIds.AllResources }],
        ContainerAccesses           = [new() { ContainerId               = WellKnownIds.AllResources }],
        AudioDeviceAccesses         = [new() { AudioDeviceId             = WellKnownIds.AllResources }],
        AgentPermissions            = [new() { AgentId                   = WellKnownIds.AllResources }],
        TaskPermissions             = [new() { ScheduledTaskId           = WellKnownIds.AllResources }],
        SkillPermissions            = [new() { SkillId                   = WellKnownIds.AllResources }],
    };

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
            RoleId = adminRole.Id
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
