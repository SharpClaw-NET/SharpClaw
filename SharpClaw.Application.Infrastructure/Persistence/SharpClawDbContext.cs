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
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence.JSON;

namespace SharpClaw.Infrastructure.Persistence;

public class SharpClawDbContext(
    DbContextOptions<SharpClawDbContext> options,
    IServiceProvider? serviceProvider = null) : DbContext(options)
{
    /// <summary>
    /// When <c>true</c>, <see cref="SaveChangesAsync"/> skips the JSON
    /// flush. Set by <see cref="JsonFilePersistenceService.LoadAsync"/>
    /// to avoid re-serialising entities that are being hydrated.
    /// </summary>
    internal bool SuppressJsonFlush { get; set; }

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
    public DbSet<InternalDatabaseDB> InternalDatabases => Set<InternalDatabaseDB>();
    public DbSet<ExternalDatabaseDB> ExternalDatabases => Set<ExternalDatabaseDB>();
    public DbSet<WebsiteDB> Websites => Set<WebsiteDB>();
    public DbSet<SearchEngineDB> SearchEngines => Set<SearchEngineDB>();
    public DbSet<ContainerDB> Containers => Set<ContainerDB>();
    public DbSet<InputAudioDB> InputAudios => Set<InputAudioDB>();
    public DbSet<DisplayDeviceDB> DisplayDevices => Set<DisplayDeviceDB>();
    public DbSet<EditorSessionDB> EditorSessions => Set<EditorSessionDB>();
    public DbSet<TranscriptionSegmentDB> TranscriptionSegments => Set<TranscriptionSegmentDB>();
    public DbSet<ClearanceUserWhitelistEntryDB> ClearanceUserWhitelistEntries => Set<ClearanceUserWhitelistEntryDB>();
    public DbSet<ClearanceAgentWhitelistEntryDB> ClearanceAgentWhitelistEntries => Set<ClearanceAgentWhitelistEntryDB>();
    public DbSet<AgentJobDB> AgentJobs => Set<AgentJobDB>();
    public DbSet<AgentJobLogEntryDB> AgentJobLogEntries => Set<AgentJobLogEntryDB>();
    public DbSet<DefaultResourceSetDB> DefaultResourceSets => Set<DefaultResourceSetDB>();
    public DbSet<ToolAwarenessSetDB> ToolAwarenessSets => Set<ToolAwarenessSetDB>();
    public DbSet<LocalModelFileDB> LocalModelFiles => Set<LocalModelFileDB>();

    // ── Bot integrations ──────────────────────────────────────────
    public DbSet<BotIntegrationDB> BotIntegrations => Set<BotIntegrationDB>();

    // ── Document sessions ─────────────────────────────────────────
    public DbSet<DocumentSessionDB> DocumentSessions => Set<DocumentSessionDB>();

    // ── Native applications ───────────────────────────────────────
    public DbSet<NativeApplicationDB> NativeApplications => Set<NativeApplicationDB>();

    // ── Generic resource access (§3.10) ───────────────────────────
    public DbSet<ResourceAccessDB> ResourceAccesses => Set<ResourceAccessDB>();

    // ── Generic global flags (§12.4.2) ────────────────────────────
    public DbSet<GlobalFlagDB> GlobalFlags => Set<GlobalFlagDB>();

    // ── Module state & config ─────────────────────────────────────
    public DbSet<ModuleStateDB> ModuleStates => Set<ModuleStateDB>();
    public DbSet<ModuleConfigEntryDB> ModuleConfigEntries => Set<ModuleConfigEntryDB>();

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
                v => v != null ? JsonSerializer.Deserialize<Dictionary<string, bool>>(v, (JsonSerializerOptions?)null)! : new())
                .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, bool>>(
                    (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                    v => v != null ? JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode() : 0,
                    v => v != null ? JsonSerializer.Deserialize<Dictionary<string, bool>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)! : new()));
        });

        // ── Agents & Chat ─────────────────────────────────────────
        modelBuilder.Entity<AgentDB>(e =>
        {
            e.HasIndex(a => a.Name).IsUnique();
            e.Property(a => a.ProviderParameters).HasConversion(
                v => v != null ? JsonSerializer.Serialize(v, (JsonSerializerOptions?)null) : null,
                v => v != null ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v, (JsonSerializerOptions?)null) : null)
                .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, JsonElement>?>(
                    (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                    v => v != null ? JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode() : 0,
                    v => v != null ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null) : null));
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
            e.HasMany(p => p.ClearanceUserWhitelist)
                .WithOne(w => w.PermissionSet)
                .HasForeignKey(w => w.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(p => p.ClearanceAgentWhitelist)
                .WithOne(w => w.PermissionSet)
                .HasForeignKey(w => w.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── GlobalFlags (§12.4.2) ────────────────────────────────
        modelBuilder.Entity<GlobalFlagDB>(e =>
        {
            e.ToTable("GlobalFlags");

            e.HasIndex(f => new { f.PermissionSetId, f.FlagKey }).IsUnique();

            e.HasOne(f => f.PermissionSet)
                .WithMany(p => p.GlobalFlags)
                .HasForeignKey(f => f.PermissionSetId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Property(f => f.Clearance).HasConversion<string>();
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
        });

        // ── Databases ─────────────────────────────────────────────
        modelBuilder.Entity<InternalDatabaseDB>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.DatabaseType).HasConversion<string>();
            e.HasOne(s => s.Skill)
                .WithMany()
                .HasForeignKey(s => s.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ExternalDatabaseDB>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.DatabaseType).HasConversion<string>();
            e.HasOne(s => s.Skill)
                .WithMany()
                .HasForeignKey(s => s.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Websites ─────────────────────────────────────────────
        modelBuilder.Entity<WebsiteDB>(e =>
        {
            e.HasIndex(w => w.Name).IsUnique();
            e.HasOne(w => w.Skill)
                .WithMany()
                .HasForeignKey(w => w.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Search engines ───────────────────────────────────────
        modelBuilder.Entity<SearchEngineDB>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Type).HasConversion<string>();
            e.HasOne(s => s.Skill)
                .WithMany()
                .HasForeignKey(s => s.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
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
        });

        // ── Input audios ─────────────────────────────────────────
        modelBuilder.Entity<InputAudioDB>(e =>
        {
            e.HasIndex(d => d.Name).IsUnique();
            e.HasOne(d => d.Skill)
                .WithMany()
                .HasForeignKey(d => d.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Display devices ──────────────────────────────────────
        modelBuilder.Entity<DisplayDeviceDB>(e =>
        {
            e.HasIndex(d => d.Name).IsUnique();
            e.HasOne(d => d.Skill)
                .WithMany()
                .HasForeignKey(d => d.SkillId)
                .OnDelete(DeleteBehavior.SetNull);
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
        });

        // ── Generic resource access (§3.10) ──────────────────────
        modelBuilder.Entity<ResourceAccessDB>(entity =>
        {
            entity.ToTable("ResourceAccesses");

            // Composite unique: a permission set cannot have duplicate grants
            // for the same resource within the same resource type and sub-type.
            entity.HasIndex(e => new { e.PermissionSetId, e.ResourceType, e.ResourceId, e.SubType })
                  .IsUnique();

            entity.HasOne(e => e.PermissionSet)
                  .WithMany(p => p.ResourceAccesses)
                  .HasForeignKey(e => e.PermissionSetId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.Clearance)
                  .HasConversion<string>();

            entity.Property(e => e.SubType)
                  .HasDefaultValue("");
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
            e.Property(j => j.Status).HasConversion<string>();
            e.Property(j => j.EffectiveClearance).HasConversion<string>();
            e.Property(j => j.DangerousShellType).HasConversion<string>();
            e.Property(j => j.SafeShellType).HasConversion<string>();
            e.Property(j => j.TranscriptionMode).HasConversion<string>();
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
        });

        // ── Document sessions ─────────────────────────────────────
        modelBuilder.Entity<DocumentSessionDB>(e =>
        {
            e.Property(d => d.DocumentType).HasConversion<string>();
        });

        // ── Native applications ───────────────────────────────────
        modelBuilder.Entity<NativeApplicationDB>(e =>
        {
            // No unique constraints or special config needed
        });

        // ── Module state & config ─────────────────────────────────
        modelBuilder.Entity<ModuleStateDB>(e =>
        {
            e.HasIndex(s => s.ModuleId).IsUnique();
        });

        modelBuilder.Entity<ModuleConfigEntryDB>(e =>
        {
            e.ToTable("ModuleConfig");
            e.HasIndex(c => new { c.ModuleId, c.Key }).IsUnique();
            e.Property(c => c.ModuleId).HasMaxLength(128);
            e.Property(c => c.Key).HasMaxLength(128);
            e.Property(c => c.Value).HasMaxLength(4096);
        });

        ConfigureForProvider(modelBuilder);
        }

    /// <summary>
    /// Applies provider-specific model configuration. Called at the end of
    /// <see cref="OnModelCreating"/> to adjust the model for provider quirks.
    /// </summary>
    private void ConfigureForProvider(ModelBuilder modelBuilder)
    {
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            // SQLite has no native DateTimeOffset support.
            // Apply a value converter on EVERY entity property of type
            // DateTimeOffset / DateTimeOffset? — this includes CreatedAt
            // and UpdatedAt which are auto-set by SaveChangesAsync on
            // every entity via the base IEntity interface.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset))
                        property.SetValueConverter(
                            new ValueConverter<DateTimeOffset, long>(
                                v => v.ToUnixTimeMilliseconds(),
                                v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
                    else if (property.ClrType == typeof(DateTimeOffset?))
                        property.SetValueConverter(
                            new ValueConverter<DateTimeOffset?, long?>(
                                v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null,
                                v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
                }
            }
        }
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

        if (!SuppressJsonFlush)
        {
            if (entityChanges.Count > 0 || joinTableChanges.Count > 0)
            {
                var jsonOpts = serviceProvider?.GetService<JsonFileOptions>();
                var flushQueue = serviceProvider?.GetService<FlushQueue>();

                // Phase K: Async flush path — enqueue intent + overlay.
                if (jsonOpts?.AsyncFlush == true && flushQueue is not null)
                {
                    var jsonSync = serviceProvider?.GetService<JsonFilePersistenceService>();
                    var serialized = new Dictionary<(string TypeName, Guid Id), byte[]>();

                    if (jsonSync is not null)
                    {
                        foreach (var (clrType, id, state) in entityChanges)
                        {
                            if (state == EntityState.Deleted)
                                continue;

                            var entity = Find(clrType, id);
                            if (entity is not null)
                            {
                                var bytes = JsonSerializer.SerializeToUtf8Bytes(entity, clrType, ColdEntityStore.JsonOptions);
                                serialized[(clrType.Name, id)] = bytes;
                            }
                        }
                    }

                    var intent = new FlushQueue.FlushIntent(entityChanges, joinTableChanges, serialized);
                    await flushQueue.EnqueueAsync(intent, cancellationToken);
                }
                else
                {
                    // Synchronous flush path (original behavior).
                    var jsonSync = serviceProvider?.GetService<JsonFilePersistenceService>();
                    if (jsonSync is not null)
                        await jsonSync.FlushChangesAsync(entityChanges, joinTableChanges, cancellationToken);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns <c>true</c> when the tracked entity is a resource-access
    /// or permission grant whose resource FK equals
    /// <see cref="WellKnownIds.AllResources"/> (the wildcard).
    /// </summary>
    private static bool IsProtectedWildcardGrant(EntityEntry entry) => entry.Entity switch
    {
        ResourceAccessDB e => e.ResourceId == WellKnownIds.AllResources,
        _ => false,
    };
}
