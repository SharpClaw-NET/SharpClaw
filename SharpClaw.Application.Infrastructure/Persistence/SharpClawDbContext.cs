using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Infrastructure.Models;
using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Application.Infrastructure.Models.Tasks;
using SharpClaw.Contracts;
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
    public DbSet<PermissionSetDB> PermissionSets => Set<PermissionSetDB>();
    public DbSet<RefreshTokenDB> RefreshTokens => Set<RefreshTokenDB>();
    public DbSet<MemoryDB> Memories => Set<MemoryDB>();
    public DbSet<ProviderDB> Providers => Set<ProviderDB>();
    public DbSet<ModelDB> Models => Set<ModelDB>();
    public DbSet<AgentDB> Agents => Set<AgentDB>();
    public DbSet<ChannelContextDB> AgentContexts => Set<ChannelContextDB>();
    public DbSet<ChannelDB> Channels => Set<ChannelDB>();
    public DbSet<ChatThreadDB> ChatThreads => Set<ChatThreadDB>();
    public DbSet<ChatMessageDB> ChatMessages => Set<ChatMessageDB>();
    public DbSet<ScheduledJobDB> ScheduledTasks => Set<ScheduledJobDB>();

    // ── Permission resources & grants ─────────────────────────────
    public DbSet<SkillDB> Skills => Set<SkillDB>();
    public DbSet<SystemUserDB> SystemUsers => Set<SystemUserDB>();
    public DbSet<DangerousShellAccessDB> DangerousShellAccesses => Set<DangerousShellAccessDB>();
    public DbSet<SafeShellAccessDB> SafeShellAccesses => Set<SafeShellAccessDB>();
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
    public DbSet<AudioDeviceDB> AudioDevices => Set<AudioDeviceDB>();
    public DbSet<AudioDeviceAccessDB> AudioDeviceAccesses => Set<AudioDeviceAccessDB>();
    public DbSet<DisplayDeviceDB> DisplayDevices => Set<DisplayDeviceDB>();
    public DbSet<DisplayDeviceAccessDB> DisplayDeviceAccesses => Set<DisplayDeviceAccessDB>();
    public DbSet<EditorSessionDB> EditorSessions => Set<EditorSessionDB>();
    public DbSet<EditorSessionAccessDB> EditorSessionAccesses => Set<EditorSessionAccessDB>();
    public DbSet<TranscriptionSegmentDB> TranscriptionSegments => Set<TranscriptionSegmentDB>();
    public DbSet<AgentManagementAccessDB> AgentPermissions => Set<AgentManagementAccessDB>();
    public DbSet<TaskManageAccessDB> TaskPermissions => Set<TaskManageAccessDB>();
    public DbSet<SkillManageAccessDB> SkillPermissions => Set<SkillManageAccessDB>();
    public DbSet<AgentHeaderAccessDB> AgentHeaderAccesses => Set<AgentHeaderAccessDB>();
    public DbSet<ChannelHeaderAccessDB> ChannelHeaderAccesses => Set<ChannelHeaderAccessDB>();
    public DbSet<ClearanceUserWhitelistEntryDB> ClearanceUserWhitelistEntries => Set<ClearanceUserWhitelistEntryDB>();
    public DbSet<ClearanceAgentWhitelistEntryDB> ClearanceAgentWhitelistEntries => Set<ClearanceAgentWhitelistEntryDB>();
    public DbSet<AgentJobDB> AgentJobs => Set<AgentJobDB>();
    public DbSet<AgentJobLogEntryDB> AgentJobLogEntries => Set<AgentJobLogEntryDB>();
    public DbSet<DefaultResourceSetDB> DefaultResourceSets => Set<DefaultResourceSetDB>();
    public DbSet<ToolAwarenessSetDB> ToolAwarenessSets => Set<ToolAwarenessSetDB>();
    public DbSet<LocalModelFileDB> LocalModelFiles => Set<LocalModelFileDB>();

    // ── Bot integrations ──────────────────────────────────────────
    public DbSet<BotIntegrationDB> BotIntegrations => Set<BotIntegrationDB>();
    public DbSet<BotIntegrationAccessDB> BotIntegrationAccesses => Set<BotIntegrationAccessDB>();

    // ── Document sessions ─────────────────────────────────────────
    public DbSet<DocumentSessionDB> DocumentSessions => Set<DocumentSessionDB>();
    public DbSet<DocumentSessionAccessDB> DocumentSessionAccesses => Set<DocumentSessionAccessDB>();

    // ── Native applications ───────────────────────────────────────
    public DbSet<NativeApplicationDB> NativeApplications => Set<NativeApplicationDB>();
    public DbSet<NativeApplicationAccessDB> NativeApplicationAccesses => Set<NativeApplicationAccessDB>();

    // ── Task scripts ──────────────────────────────────────────────
    public DbSet<TaskDefinitionDB> TaskDefinitions => Set<TaskDefinitionDB>();
    public DbSet<TaskInstanceDB> TaskInstances => Set<TaskInstanceDB>();
    public DbSet<TaskExecutionLogDB> TaskExecutionLogs => Set<TaskExecutionLogDB>();
    public DbSet<TaskOutputEntryDB> TaskOutputEntries => Set<TaskOutputEntryDB>();

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
            e.HasOne(r => r.PermissionSet)
                .WithMany()
                .HasForeignKey(r => r.PermissionSetId)
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
            e.Property(m => m.Capabilities).HasConversion<string>();
            e.HasMany(m => m.Agents)
                .WithOne(a => a.Model)
                .HasForeignKey(a => a.ModelId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LocalModelFileDB>(e =>
        {
            e.HasIndex(f => f.ModelId).IsUnique();
            e.Property(f => f.Status).HasConversion<string>();
            e.HasOne(f => f.Model)
                .WithMany()
                .HasForeignKey(f => f.ModelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Tool Awareness Sets ────────────────────────────────────
        modelBuilder.Entity<ToolAwarenessSetDB>(e =>
        {
            e.HasIndex(t => t.Name).IsUnique();
            e.Property(t => t.Tools).HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v != null ? JsonSerializer.Deserialize<Dictionary<string, bool>>(v, (JsonSerializerOptions?)null)! : new());
        });

        // ── Agents & Chat ─────────────────────────────────────────
        modelBuilder.Entity<AgentDB>(e =>
        {
            e.HasIndex(a => a.Name).IsUnique();
            e.Property(a => a.ProviderParameters).HasConversion(
                v => v != null ? JsonSerializer.Serialize(v, (JsonSerializerOptions?)null) : null,
                v => v != null ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v, (JsonSerializerOptions?)null) : null);
            e.HasMany(a => a.Contexts)
                .WithOne(c => c.Agent)
                .HasForeignKey(c => c.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Channels)
                .WithOne(c => c.Agent!)
                .HasForeignKey(c => c.AgentId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(a => a.Role)
                .WithMany()
                .HasForeignKey(a => a.RoleId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(a => a.ToolAwarenessSet)
                .WithMany()
                .HasForeignKey(a => a.ToolAwarenessSetId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Agent Contexts ────────────────────────────────────────
        modelBuilder.Entity<ChannelContextDB>(e =>
        {
            e.HasIndex(c => new { c.AgentId, c.Name }).IsUnique();
            e.HasMany(c => c.Channels)
                .WithOne(conv => conv.AgentContext!)
                .HasForeignKey(conv => conv.AgentContextId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(c => c.Tasks)
                .WithOne(t => t.AgentContext!)
                .HasForeignKey(t => t.AgentContextId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.PermissionSet)
                .WithMany()
                .HasForeignKey(c => c.PermissionSetId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.DefaultResourceSet)
                .WithMany()
                .HasForeignKey(c => c.DefaultResourceSetId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(c => c.AllowedAgents)
                .WithMany(a => a.AllowedContexts)
                .UsingEntity("ContextAllowedAgents");
        });

        // ── Channels ──────────────────────────────────────────────
        modelBuilder.Entity<ChannelDB>(e =>
        {
            e.HasMany(c => c.ChatMessages)
                .WithOne(m => m.Channel)
                .HasForeignKey(m => m.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.PermissionSet)
                .WithMany()
                .HasForeignKey(c => c.PermissionSetId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.DefaultResourceSet)
                .WithMany()
                .HasForeignKey(c => c.DefaultResourceSetId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.ToolAwarenessSet)
                .WithMany()
                .HasForeignKey(c => c.ToolAwarenessSetId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(c => c.AllowedAgents)
                .WithMany(a => a.AllowedChannels)
                .UsingEntity("ChannelAllowedAgents");
            e.HasMany(c => c.Threads)
                .WithOne(t => t.Channel)
                .HasForeignKey(t => t.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Chat Threads ──────────────────────────────────────────
        modelBuilder.Entity<ChatThreadDB>(e =>
        {
            e.HasMany(t => t.ChatMessages)
                .WithOne(m => m.Thread!)
                .HasForeignKey(m => m.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Chat Messages ─────────────────────────────────────────
        modelBuilder.Entity<ChatMessageDB>(e =>
        {
            e.Property(m => m.ClientType).HasConversion<string>();
        });

        // ── Scheduled Tasks ───────────────────────────────────────
        modelBuilder.Entity<ScheduledJobDB>(e =>
        {
            e.HasOne(t => t.PermissionSet)
                .WithMany()
                .HasForeignKey(t => t.PermissionSetId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── PermissionSets ────────────────────────────────────────
        modelBuilder.Entity<PermissionSetDB>(e =>
        {
            e.Property(p => p.DefaultClearance).HasConversion<string>();

            e.HasMany(p => p.DangerousShellAccesses)
                .WithOne(s => s.PermissionSet)
                .HasForeignKey(s => s.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.SafeShellAccesses)
                .WithOne(s => s.PermissionSet)
                .HasForeignKey(s => s.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.LocalInfoStorePermissions)
                .WithOne(l => l.PermissionSet)
                .HasForeignKey(l => l.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ExternalInfoStorePermissions)
                .WithOne(x => x.PermissionSet)
                .HasForeignKey(x => x.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.WebsiteAccesses)
                .WithOne(w => w.PermissionSet)
                .HasForeignKey(w => w.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.SearchEngineAccesses)
                .WithOne(s => s.PermissionSet)
                .HasForeignKey(s => s.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ContainerAccesses)
                .WithOne(c => c.PermissionSet)
                .HasForeignKey(c => c.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.AudioDeviceAccesses)
                .WithOne(a => a.PermissionSet)
                .HasForeignKey(a => a.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.DisplayDeviceAccesses)
                .WithOne(a => a.PermissionSet)
                .HasForeignKey(a => a.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.EditorSessionAccesses)
                .WithOne(a => a.PermissionSet)
                .HasForeignKey(a => a.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.AgentPermissions)
                .WithOne(a => a.PermissionSet)
                .HasForeignKey(a => a.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.TaskPermissions)
                .WithOne(t => t.PermissionSet)
                .HasForeignKey(t => t.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.SkillPermissions)
                .WithOne(s => s.PermissionSet)
                .HasForeignKey(s => s.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.AgentHeaderAccesses)
                .WithOne(a => a.PermissionSet)
                .HasForeignKey(a => a.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ChannelHeaderAccesses)
                .WithOne(c => c.PermissionSet)
                .HasForeignKey(c => c.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.BotIntegrationAccesses)
                .WithOne(b => b.PermissionSet)
                .HasForeignKey(b => b.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ClearanceUserWhitelist)
                .WithOne(w => w.PermissionSet)
                .HasForeignKey(w => w.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ClearanceAgentWhitelist)
                .WithOne(w => w.PermissionSet)
                .HasForeignKey(w => w.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            // ── Default resource access FKs ───────────────────────
            e.HasOne(p => p.DefaultDangerousShellAccess)
                .WithMany()
                .HasForeignKey(p => p.DefaultDangerousShellAccessId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultSafeShellAccess)
                .WithMany()
                .HasForeignKey(p => p.DefaultSafeShellAccessId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultLocalInfoStorePermission)
                .WithMany()
                .HasForeignKey(p => p.DefaultLocalInfoStorePermissionId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultExternalInfoStorePermission)
                .WithMany()
                .HasForeignKey(p => p.DefaultExternalInfoStorePermissionId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultWebsiteAccess)
                .WithMany()
                .HasForeignKey(p => p.DefaultWebsiteAccessId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultSearchEngineAccess)
                .WithMany()
                .HasForeignKey(p => p.DefaultSearchEngineAccessId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultContainerAccess)
                .WithMany()
                .HasForeignKey(p => p.DefaultContainerAccessId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultAudioDeviceAccess)
                .WithMany()
                .HasForeignKey(p => p.DefaultAudioDeviceAccessId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultDisplayDeviceAccess)
                .WithMany()
                .HasForeignKey(p => p.DefaultDisplayDeviceAccessId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultEditorSessionAccess)
                .WithMany()
                .HasForeignKey(p => p.DefaultEditorSessionAccessId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultAgentPermission)
                .WithMany()
                .HasForeignKey(p => p.DefaultAgentPermissionId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultTaskPermission)
                .WithMany()
                .HasForeignKey(p => p.DefaultTaskPermissionId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultSkillPermission)
                .WithMany()
                .HasForeignKey(p => p.DefaultSkillPermissionId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.DefaultBotIntegrationAccess)
                .WithMany()
                .HasForeignKey(p => p.DefaultBotIntegrationAccessId)
                .OnDelete(DeleteBehavior.SetNull);
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
            e.HasMany(s => s.DangerousShellAccesses)
                .WithOne(a => a.SystemUser)
                .HasForeignKey(a => a.SystemUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DangerousShellAccessDB>(e =>
        {
            e.HasIndex(a => new { a.PermissionSetId, a.SystemUserId, a.ShellType }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
            e.Property(a => a.ShellType).HasConversion<string>();
        });

        modelBuilder.Entity<SafeShellAccessDB>(e =>
        {
            e.HasIndex(a => new { a.PermissionSetId, a.ContainerId, a.ShellType }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
            e.Property(a => a.ShellType).HasConversion<string>();
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
            e.HasIndex(p => new { p.PermissionSetId, p.LocalInformationStoreId }).IsUnique();
            e.Property(p => p.AccessLevel).HasConversion<string>();
            e.Property(p => p.Clearance).HasConversion<string>();
        });

        modelBuilder.Entity<ExternalInfoStoreAccessDB>(e =>
        {
            e.HasIndex(p => new { p.PermissionSetId, p.ExternalInformationStoreId }).IsUnique();
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
            e.HasIndex(a => new { a.PermissionSetId, a.WebsiteId }).IsUnique();
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
            e.HasIndex(a => new { a.PermissionSetId, a.SearchEngineId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Containers ───────────────────────────────────────────
        modelBuilder.Entity<ContainerDB>(e =>
        {
            e.HasIndex(c => c.Name).IsUnique();
            e.Property(c => c.Type).HasConversion<string>();
            e.HasOne(c => c.Skill)
                .WithMany()
                .HasForeignKey(c => c.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(c => c.Accesses)
                .WithOne(a => a.Container)
                .HasForeignKey(a => a.ContainerId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.SafeShellAccesses)
                .WithOne(a => a.Container)
                .HasForeignKey(a => a.ContainerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContainerAccessDB>(e =>
        {
            e.HasIndex(a => new { a.PermissionSetId, a.ContainerId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Audio devices ────────────────────────────────────────
        modelBuilder.Entity<AudioDeviceDB>(e =>
        {
            e.HasIndex(d => d.Name).IsUnique();
            e.HasOne(d => d.Skill)
                .WithMany()
                .HasForeignKey(d => d.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(d => d.Accesses)
                .WithOne(a => a.AudioDevice)
                .HasForeignKey(a => a.AudioDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AudioDeviceAccessDB>(e =>
        {
            e.HasIndex(a => new { a.PermissionSetId, a.AudioDeviceId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Display devices ──────────────────────────────────────
        modelBuilder.Entity<DisplayDeviceDB>(e =>
        {
            e.HasIndex(d => d.Name).IsUnique();
            e.HasOne(d => d.Skill)
                .WithMany()
                .HasForeignKey(d => d.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(d => d.Accesses)
                .WithOne(a => a.DisplayDevice)
                .HasForeignKey(a => a.DisplayDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DisplayDeviceAccessDB>(e =>
        {
            e.HasIndex(a => new { a.PermissionSetId, a.DisplayDeviceId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Editor sessions ──────────────────────────────────────
        modelBuilder.Entity<EditorSessionDB>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.EditorType).HasConversion<string>();
            e.HasOne(s => s.Skill)
                .WithMany()
                .HasForeignKey(s => s.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(s => s.Accesses)
                .WithOne(a => a.EditorSession)
                .HasForeignKey(a => a.EditorSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditorSessionAccessDB>(e =>
        {
            e.HasIndex(a => new { a.PermissionSetId, a.EditorSessionId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Agent & Task permissions ──────────────────────────────
        modelBuilder.Entity<AgentManagementAccessDB>(e =>
        {
            e.HasIndex(p => new { p.PermissionSetId, p.AgentId }).IsUnique();
            e.Property(p => p.Clearance).HasConversion<string>();
            e.HasOne(p => p.Agent)
                .WithMany()
                .HasForeignKey(p => p.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskManageAccessDB>(e =>
        {
            e.HasIndex(p => new { p.PermissionSetId, p.ScheduledTaskId }).IsUnique();
            e.Property(p => p.Clearance).HasConversion<string>();
            e.HasOne(p => p.ScheduledTask)
                .WithMany()
                .HasForeignKey(p => p.ScheduledTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Skill permissions ─────────────────────────────────────
        modelBuilder.Entity<SkillManageAccessDB>(e =>
        {
            e.HasIndex(p => new { p.PermissionSetId, p.SkillId }).IsUnique();
            e.Property(p => p.Clearance).HasConversion<string>();
            e.HasOne(p => p.Skill)
                .WithMany()
                .HasForeignKey(p => p.SkillId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Clearance whitelists ──────────────────────────────────
        modelBuilder.Entity<ClearanceUserWhitelistEntryDB>(e =>
        {
            e.HasIndex(w => new { w.PermissionSetId, w.UserId }).IsUnique();
            e.HasOne(w => w.User)
                .WithMany()
                .HasForeignKey(w => w.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClearanceAgentWhitelistEntryDB>(e =>
        {
            e.HasIndex(w => new { w.PermissionSetId, w.AgentId }).IsUnique();
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
            e.Property(j => j.DangerousShellType).HasConversion<string>();
            e.Property(j => j.SafeShellType).HasConversion<string>();
            e.HasOne(j => j.Agent)
                .WithMany()
                .HasForeignKey(j => j.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(j => j.LogEntries)
                .WithOne(l => l.AgentJob)
                .HasForeignKey(l => l.AgentJobId)
                .OnDelete(DeleteBehavior.Cascade);
            // Transcription
            e.HasOne(j => j.TranscriptionModel)
                .WithMany()
                .HasForeignKey(j => j.TranscriptionModelId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(j => j.Channel)
                .WithMany()
                .HasForeignKey(j => j.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(j => j.TranscriptionSegments)
                .WithOne(s => s.AgentJob)
                .HasForeignKey(s => s.AgentJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Task definitions & instances ──────────────────────────
        modelBuilder.Entity<TaskDefinitionDB>(e =>
        {
            e.HasIndex(d => d.Name).IsUnique();
            e.HasMany(d => d.Instances)
                .WithOne(i => i.TaskDefinition)
                .HasForeignKey(i => i.TaskDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskInstanceDB>(e =>
        {
            e.Property(i => i.Status).HasConversion<string>();
            e.HasOne(i => i.Channel)
                .WithMany()
                .HasForeignKey(i => i.ChannelId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasMany(i => i.LogEntries)
                .WithOne(l => l.TaskInstance)
                .HasForeignKey(l => l.TaskInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(i => i.OutputEntries)
                .WithOne(o => o.TaskInstance)
                .HasForeignKey(o => o.TaskInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Bot integrations ──────────────────────────────────
        modelBuilder.Entity<BotIntegrationDB>(e =>
        {
            e.HasIndex(b => b.BotType).IsUnique();
            e.Property(b => b.BotType).HasConversion<string>();
            e.HasMany(b => b.Accesses)
                .WithOne(a => a.BotIntegration)
                .HasForeignKey(a => a.BotIntegrationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BotIntegrationAccessDB>(e =>
        {
            e.HasIndex(a => new { a.PermissionSetId, a.BotIntegrationId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Document sessions ─────────────────────────────────────
        modelBuilder.Entity<DocumentSessionDB>(e =>
        {
            e.Property(d => d.DocumentType).HasConversion<string>();
            e.HasMany(d => d.Accesses)
                .WithOne(a => a.DocumentSession)
                .HasForeignKey(a => a.DocumentSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentSessionAccessDB>(e =>
        {
            e.HasIndex(a => new { a.PermissionSetId, a.DocumentSessionId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        // ── Native applications ───────────────────────────────────
        modelBuilder.Entity<NativeApplicationDB>(e =>
        {
            e.HasMany(n => n.Accesses)
                .WithOne(a => a.NativeApplication)
                .HasForeignKey(a => a.NativeApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NativeApplicationAccessDB>(e =>
        {
            e.HasIndex(a => new { a.PermissionSetId, a.NativeApplicationId }).IsUnique();
            e.Property(a => a.Clearance).HasConversion<string>();
        });

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SharpClawDbContext).Assembly);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Wildcard resource grants are immutable — reject any attempt
        // to modify or delete them at runtime.
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted
                && IsProtectedWildcardGrant(entry))
            {
                throw new InvalidOperationException(
                    "Wildcard resource grants (AllResources) are immutable and cannot be modified or deleted.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            var now = DateTimeOffset.UtcNow;

            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default)
                    entry.Entity.CreatedAt = now;
                if (entry.Entity.UpdatedAt == default)
                    entry.Entity.UpdatedAt = now;

                if (entry.Entity.Id == Guid.Empty)
                    entry.Entity.Id = Guid.NewGuid();
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        // Capture pending changes BEFORE committing to the InMemory store.
        // After base.SaveChangesAsync the change tracker resets states to
        // Unchanged and we lose the information about what was modified.
        var entityChanges = new List<(Type ClrType, Guid Id, EntityState State)>();
        var joinTableChanges = new HashSet<string>();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is BaseEntity be
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                entityChanges.Add((be.GetType(), be.Id, entry.State));
            }
            else if (entry.Metadata.HasSharedClrType
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                joinTableChanges.Add(entry.Metadata.Name);
            }
        }

        var result = await base.SaveChangesAsync(cancellationToken);

        var jsonSync = serviceProvider?.GetService<JsonFilePersistenceService>();
        if (jsonSync is not null && (entityChanges.Count > 0 || joinTableChanges.Count > 0))
            await jsonSync.FlushChangesAsync(entityChanges, joinTableChanges, cancellationToken);

        return result;
    }

    /// <summary>
    /// Returns <c>true</c> when the tracked entity is a resource-access
    /// or permission grant whose resource FK equals
    /// <see cref="WellKnownIds.AllResources"/> (the wildcard).
    /// </summary>
    private static bool IsProtectedWildcardGrant(EntityEntry entry) => entry.Entity switch
    {
        DangerousShellAccessDB      e => e.SystemUserId              == WellKnownIds.AllResources,
        SafeShellAccessDB           e => e.ContainerId              == WellKnownIds.AllResources,
        LocalInfoStoreAccessDB      e => e.LocalInformationStoreId   == WellKnownIds.AllResources,
        ExternalInfoStoreAccessDB   e => e.ExternalInformationStoreId == WellKnownIds.AllResources,
        WebsiteAccessDB             e => e.WebsiteId                 == WellKnownIds.AllResources,
        SearchEngineAccessDB        e => e.SearchEngineId            == WellKnownIds.AllResources,
        ContainerAccessDB           e => e.ContainerId               == WellKnownIds.AllResources,
        AudioDeviceAccessDB         e => e.AudioDeviceId             == WellKnownIds.AllResources,
        DisplayDeviceAccessDB       e => e.DisplayDeviceId           == WellKnownIds.AllResources,
        EditorSessionAccessDB       e => e.EditorSessionId           == WellKnownIds.AllResources,
        AgentManagementAccessDB           e => e.AgentId                   == WellKnownIds.AllResources,
        TaskManageAccessDB            e => e.ScheduledTaskId           == WellKnownIds.AllResources,
        SkillManageAccessDB           e => e.SkillId                   == WellKnownIds.AllResources,
        DocumentSessionAccessDB       e => e.DocumentSessionId           == WellKnownIds.AllResources,
        NativeApplicationAccessDB     e => e.NativeApplicationId         == WellKnownIds.AllResources,
        _ => false,
    };
}
