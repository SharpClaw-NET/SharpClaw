using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence.JSON;

namespace SharpClaw.Infrastructure.Persistence;

public class SharpClawDbContext(
    DbContextOptions<SharpClawDbContext> options,
    IServiceProvider? serviceProvider = null) : DbContext(options)
{
    public DbSet<UserDB> Users => Set<UserDB>();
    public DbSet<RoleDB> Roles => Set<RoleDB>();
    public DbSet<RolePermissionsDB> RolePermissions => Set<RolePermissionsDB>();
    public DbSet<RefreshTokenDB> RefreshTokens => Set<RefreshTokenDB>();
    public DbSet<MemoryDB> Memories => Set<MemoryDB>();
    public DbSet<ProviderDB> Providers => Set<ProviderDB>();
    public DbSet<ModelDB> Models => Set<ModelDB>();
    public DbSet<AgentDB> Agents => Set<AgentDB>();
    public DbSet<AgentContextDB> AgentContexts => Set<AgentContextDB>();
    public DbSet<ContextPermissionGrantDB> ContextPermissionGrants => Set<ContextPermissionGrantDB>();
    public DbSet<ConversationDB> Conversations => Set<ConversationDB>();
    public DbSet<ConversationPermissionGrantDB> ConversationPermissionGrants => Set<ConversationPermissionGrantDB>();
    public DbSet<TaskPermissionGrantDB> TaskPermissionGrants => Set<TaskPermissionGrantDB>();
    public DbSet<ChatMessageDB> ChatMessages => Set<ChatMessageDB>();
    public DbSet<ScheduledTaskDB> ScheduledTasks => Set<ScheduledTaskDB>();

    // ── Permission resources & grants ─────────────────────────────
    public DbSet<SkillDB> Skills => Set<SkillDB>();
    public DbSet<SystemUserDB> SystemUsers => Set<SystemUserDB>();
    public DbSet<SystemUserAccessDB> SystemUserAccesses => Set<SystemUserAccessDB>();
    public DbSet<LocalInformationStoreDB> LocalInformationStores => Set<LocalInformationStoreDB>();
    public DbSet<ExternalInformationStoreDB> ExternalInformationStores => Set<ExternalInformationStoreDB>();
    public DbSet<LocalInfoStoreAccessDB> LocalInfoStorePermissions => Set<LocalInfoStoreAccessDB>();
    public DbSet<ExternalInfoStoreAccessDB> ExternalInfoStorePermissions => Set<ExternalInfoStoreAccessDB>();
    public DbSet<WebsiteDB> Websites => Set<WebsiteDB>();
    public DbSet<WebsiteAccessDB> WebsiteAccesses => Set<WebsiteAccessDB>();
    public DbSet<SearchEngineDB> SearchEngines => Set<SearchEngineDB>();
    public DbSet<SearchEngineAccessDB> SearchEngineAccesses => Set<SearchEngineAccessDB>();
    public DbSet<ContainerDB> Containers => Set<ContainerDB>();
    public DbSet<ContainerAccessDB> ContainerAccesses => Set<ContainerAccessDB>();
    public DbSet<AgentPermissionDB> AgentPermissions => Set<AgentPermissionDB>();
    public DbSet<TaskPermissionDB> TaskPermissions => Set<TaskPermissionDB>();
    public DbSet<SkillPermissionDB> SkillPermissions => Set<SkillPermissionDB>();
    public DbSet<ClearanceUserWhitelistEntryDB> ClearanceUserWhitelistEntries => Set<ClearanceUserWhitelistEntryDB>();
    public DbSet<ClearanceAgentWhitelistEntryDB> ClearanceAgentWhitelistEntries => Set<ClearanceAgentWhitelistEntryDB>();
    public DbSet<AgentJobDB> AgentJobs => Set<AgentJobDB>();
    public DbSet<AgentJobLogEntryDB> AgentJobLogEntries => Set<AgentJobLogEntryDB>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Roles & Users ─────────────────────────────────────────
        modelBuilder.Entity<RoleDB>(e =>
        {
            e.HasIndex(r => r.Name).IsUnique();
            e.HasMany(r => r.Users)
                .WithOne(u => u.Role)
                .HasForeignKey(u => u.RoleId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.Permissions)
                .WithOne(p => p.Role)
                .HasForeignKey<RolePermissionsDB>(p => p.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserDB>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.HasMany(u => u.RefreshTokens)
                .WithOne(r => r.User)
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshTokenDB>(e =>
        {
            e.HasIndex(r => r.Token).IsUnique();
        });

        // ── Providers & Models ────────────────────────────────────
        modelBuilder.Entity<ProviderDB>(e =>
        {
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.ProviderType).HasConversion<string>();
            e.HasMany(p => p.Models)
                .WithOne(m => m.Provider)
                .HasForeignKey(m => m.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModelDB>(e =>
        {
            e.HasIndex(m => new { m.Name, m.ProviderId }).IsUnique();
            e.HasMany(m => m.Agents)
                .WithOne(a => a.Model)
                .HasForeignKey(a => a.ModelId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Agents & Chat ─────────────────────────────────────────
        modelBuilder.Entity<AgentDB>(e =>
        {
            e.HasIndex(a => a.Name).IsUnique();
            e.HasMany(a => a.Contexts)
                .WithOne(c => c.Agent)
                .HasForeignKey(c => c.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Conversations)
                .WithOne(c => c.Agent)
                .HasForeignKey(c => c.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Role)
                .WithMany()
                .HasForeignKey(a => a.RoleId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Agent Contexts ────────────────────────────────────────
        modelBuilder.Entity<AgentContextDB>(e =>
        {
            e.HasIndex(c => new { c.AgentId, c.Name }).IsUnique();
            e.HasMany(c => c.Conversations)
                .WithOne(conv => conv.AgentContext!)
                .HasForeignKey(conv => conv.AgentContextId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(c => c.Tasks)
                .WithOne(t => t.AgentContext!)
                .HasForeignKey(t => t.AgentContextId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(c => c.PermissionGrants)
                .WithOne(g => g.AgentContext)
                .HasForeignKey(g => g.AgentContextId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContextPermissionGrantDB>(e =>
        {
            e.HasIndex(g => new { g.AgentContextId, g.ActionType }).IsUnique();
            e.Property(g => g.ActionType).HasConversion<string>();
            e.Property(g => g.GrantedClearance).HasConversion<string>();
        });

        // ── Conversations ─────────────────────────────────────────
        modelBuilder.Entity<ConversationDB>(e =>
        {
            e.HasOne(c => c.Model)
                .WithMany(m => m.Conversations)
                .HasForeignKey(c => c.ModelId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(c => c.ChatMessages)
                .WithOne(m => m.Conversation)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.PermissionGrants)
                .WithOne(g => g.Conversation)
                .HasForeignKey(g => g.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationPermissionGrantDB>(e =>
        {
            e.HasIndex(g => new { g.ConversationId, g.ActionType }).IsUnique();
            e.Property(g => g.ActionType).HasConversion<string>();
            e.Property(g => g.GrantedClearance).HasConversion<string>();
        });

        // ── Scheduled Tasks ───────────────────────────────────────
        modelBuilder.Entity<ScheduledTaskDB>(e =>
        {
            e.HasMany(t => t.PermissionGrants)
                .WithOne(g => g.ScheduledTask)
                .HasForeignKey(g => g.ScheduledTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskPermissionGrantDB>(e =>
        {
            e.HasIndex(g => new { g.ScheduledTaskId, g.ActionType }).IsUnique();
            e.Property(g => g.ActionType).HasConversion<string>();
            e.Property(g => g.GrantedClearance).HasConversion<string>();
        });

        // ── RolePermissions ───────────────────────────────────────
        modelBuilder.Entity<RolePermissionsDB>(e =>
        {
            e.HasIndex(p => p.RoleId).IsUnique();
            e.Property(p => p.DefaultClearance).HasConversion<string>();

            e.HasMany(p => p.SystemUserAccesses)
                .WithOne(s => s.RolePermissions)
                .HasForeignKey(s => s.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.LocalInfoStorePermissions)
                .WithOne(l => l.RolePermissions)
                .HasForeignKey(l => l.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ExternalInfoStorePermissions)
                .WithOne(x => x.RolePermissions)
                .HasForeignKey(x => x.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.WebsiteAccesses)
                .WithOne(w => w.RolePermissions)
                .HasForeignKey(w => w.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.SearchEngineAccesses)
                .WithOne(s => s.RolePermissions)
                .HasForeignKey(s => s.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ContainerAccesses)
                .WithOne(c => c.RolePermissions)
                .HasForeignKey(c => c.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.AgentPermissions)
                .WithOne(a => a.RolePermissions)
                .HasForeignKey(a => a.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.TaskPermissions)
                .WithOne(t => t.RolePermissions)
                .HasForeignKey(t => t.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.SkillPermissions)
                .WithOne(s => s.RolePermissions)
                .HasForeignKey(s => s.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ClearanceUserWhitelist)
                .WithOne(w => w.RolePermissions)
                .HasForeignKey(w => w.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ClearanceAgentWhitelist)
                .WithOne(w => w.RolePermissions)
                .HasForeignKey(w => w.RolePermissionsId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Skills ───────────────────────────────────────────────
        modelBuilder.Entity<SkillDB>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
        });

        // ── System users ─────────────────────────────────────────
        modelBuilder.Entity<SystemUserDB>(e =>
        {
            e.HasIndex(s => s.Username).IsUnique();
            e.HasOne(s => s.Skill)
                .WithMany()
                .HasForeignKey(s => s.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(s => s.Accesses)
                .WithOne(a => a.SystemUser)
                .HasForeignKey(a => a.SystemUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SystemUserAccessDB>(e =>
        {
            e.HasIndex(a => new { a.RolePermissionsId, a.SystemUserId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Information stores ────────────────────────────────────
        modelBuilder.Entity<LocalInformationStoreDB>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.HasOne(s => s.Skill)
                .WithMany()
                .HasForeignKey(s => s.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(s => s.Permissions)
                .WithOne(p => p.LocalInformationStore)
                .HasForeignKey(p => p.LocalInformationStoreId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalInformationStoreDB>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.HasOne(s => s.Skill)
                .WithMany()
                .HasForeignKey(s => s.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(s => s.Permissions)
                .WithOne(p => p.ExternalInformationStore)
                .HasForeignKey(p => p.ExternalInformationStoreId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LocalInfoStoreAccessDB>(e =>
        {
            e.HasIndex(p => new { p.RolePermissionsId, p.LocalInformationStoreId }).IsUnique();
            e.Property(p => p.AccessLevel).HasConversion<string>();
            e.Property(p => p.Clearance).HasConversion<string>();
        });

        modelBuilder.Entity<ExternalInfoStoreAccessDB>(e =>
        {
            e.HasIndex(p => new { p.RolePermissionsId, p.ExternalInformationStoreId }).IsUnique();
            e.Property(p => p.AccessLevel).HasConversion<string>();
            e.Property(p => p.Clearance).HasConversion<string>();
        });

        // ── Websites ─────────────────────────────────────────────
        modelBuilder.Entity<WebsiteDB>(e =>
        {
            e.HasIndex(w => w.Name).IsUnique();
            e.HasOne(w => w.Skill)
                .WithMany()
                .HasForeignKey(w => w.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(w => w.Accesses)
                .WithOne(a => a.Website)
                .HasForeignKey(a => a.WebsiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebsiteAccessDB>(e =>
        {
            e.HasIndex(a => new { a.RolePermissionsId, a.WebsiteId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Search engines ───────────────────────────────────────
        modelBuilder.Entity<SearchEngineDB>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.HasOne(s => s.Skill)
                .WithMany()
                .HasForeignKey(s => s.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(s => s.Accesses)
                .WithOne(a => a.SearchEngine)
                .HasForeignKey(a => a.SearchEngineId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SearchEngineAccessDB>(e =>
        {
            e.HasIndex(a => new { a.RolePermissionsId, a.SearchEngineId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Containers ───────────────────────────────────────────
        modelBuilder.Entity<ContainerDB>(e =>
        {
            e.HasIndex(c => c.Name).IsUnique();
            e.HasOne(c => c.Skill)
                .WithMany()
                .HasForeignKey(c => c.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(c => c.Accesses)
                .WithOne(a => a.Container)
                .HasForeignKey(a => a.ContainerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContainerAccessDB>(e =>
        {
            e.HasIndex(a => new { a.RolePermissionsId, a.ContainerId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Agent & Task permissions ──────────────────────────────
        modelBuilder.Entity<AgentPermissionDB>(e =>
        {
            e.HasIndex(p => new { p.RolePermissionsId, p.AgentId }).IsUnique();
            e.Property(p => p.Clearance).HasConversion<string>();
            e.HasOne(p => p.Agent)
                .WithMany()
                .HasForeignKey(p => p.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskPermissionDB>(e =>
        {
            e.HasIndex(p => new { p.RolePermissionsId, p.ScheduledTaskId }).IsUnique();
            e.Property(p => p.Clearance).HasConversion<string>();
            e.HasOne(p => p.ScheduledTask)
                .WithMany()
                .HasForeignKey(p => p.ScheduledTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Skill permissions ─────────────────────────────────────
        modelBuilder.Entity<SkillPermissionDB>(e =>
        {
            e.HasIndex(p => new { p.RolePermissionsId, p.SkillId }).IsUnique();
            e.Property(p => p.Clearance).HasConversion<string>();
            e.HasOne(p => p.Skill)
                .WithMany()
                .HasForeignKey(p => p.SkillId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Clearance whitelists ──────────────────────────────────
        modelBuilder.Entity<ClearanceUserWhitelistEntryDB>(e =>
        {
            e.HasIndex(w => new { w.RolePermissionsId, w.UserId }).IsUnique();
            e.HasOne(w => w.User)
                .WithMany()
                .HasForeignKey(w => w.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClearanceAgentWhitelistEntryDB>(e =>
        {
            e.HasIndex(w => new { w.RolePermissionsId, w.AgentId }).IsUnique();
            e.HasOne(w => w.Agent)
                .WithMany()
                .HasForeignKey(w => w.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Agent jobs ────────────────────────────────────────────
        modelBuilder.Entity<AgentJobDB>(e =>
        {
            e.Property(j => j.ActionType).HasConversion<string>();
            e.Property(j => j.Status).HasConversion<string>();
            e.Property(j => j.EffectiveClearance).HasConversion<string>();
            e.HasOne(j => j.Agent)
                .WithMany()
                .HasForeignKey(j => j.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(j => j.LogEntries)
                .WithOne(l => l.AgentJob)
                .HasForeignKey(l => l.AgentJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SharpClawDbContext).Assembly);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            var now = DateTimeOffset.UtcNow;

            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;

                if (entry.Entity.Id == Guid.Empty)
                    entry.Entity.Id = Guid.NewGuid();
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        var result = await base.SaveChangesAsync(cancellationToken);

        var jsonSync = serviceProvider?.GetService<JsonFilePersistenceService>();
        if (jsonSync is not null)
            await jsonSync.FlushAsync(cancellationToken);

        return result;
    }
}
