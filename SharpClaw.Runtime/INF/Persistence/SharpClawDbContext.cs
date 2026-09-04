using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Runtime.INF.Persistence;

public class SharpClawDbContext(
    DbContextOptions<SharpClawDbContext> options,
    IRuntimePersistenceActionRunnerAccessor? persistenceActionRunnerAccessor = null)
    : DbContext(options), ISharpClawDataContext
{
    private readonly IRuntimePersistenceActionRunnerAccessor? _persistenceActionRunnerAccessor =
        persistenceActionRunnerAccessor;

    IQueryable<ProviderDB> ISharpClawDataContext.Providers => Providers;
    IQueryable<ModelDB> ISharpClawDataContext.Models => Models;
    IQueryable<RegistrationStateDB> ISharpClawDataContext.RegistrationStates => RegistrationStates;
    IQueryable<ConfigurationEntryDB> ISharpClawDataContext.ConfigurationEntries => ConfigurationEntries;
    IQueryable<ScopedStorageRecordDB> ISharpClawDataContext.ScopedStorageRecords => ScopedStorageRecords;
    IQueryable<ScopedStorageIndexEntryDB> ISharpClawDataContext.ScopedStorageIndexEntries => ScopedStorageIndexEntries;

    public DbSet<ProviderDB> Providers => Set<ProviderDB>();
    public DbSet<ModelDB> Models => Set<ModelDB>();
    public DbSet<RegistrationStateDB> RegistrationStates => Set<RegistrationStateDB>();
    public DbSet<ConfigurationEntryDB> ConfigurationEntries => Set<ConfigurationEntryDB>();
    public DbSet<ScopedStorageRecordDB> ScopedStorageRecords => Set<ScopedStorageRecordDB>();
    public DbSet<ScopedStorageIndexEntryDB> ScopedStorageIndexEntries => Set<ScopedStorageIndexEntryDB>();

    internal Task<int> SaveChangesThroughKernelAsync(
        CancellationToken cancellationToken = default) =>
        _persistenceActionRunnerAccessor is { } accessor
            ? accessor.GetRequiredRunner().SaveChangesAsync(this, cancellationToken).AsTask()
            : throw new InvalidOperationException(
                "The Runtime persistence action runner is not configured.");

    internal Task<int> SaveChangesTerminalAsync(CancellationToken cancellationToken) =>
        SaveChangesCoreAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ProviderDB>(entity =>
        {
            entity.HasIndex(provider => provider.Name).IsUnique();
            entity.HasMany(provider => provider.Models)
                .WithOne(model => model.Provider)
                .HasForeignKey(model => model.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModelDB>(entity =>
        {
            entity.HasIndex(model => new { model.Name, model.ProviderId }).IsUnique();
        });

        modelBuilder.Entity<RegistrationStateDB>(entity =>
        {
            entity.HasIndex(state => state.SourceId).IsUnique();
        });

        modelBuilder.Entity<ConfigurationEntryDB>(entity =>
        {
            entity.ToTable("Configuration");
            entity.HasIndex(entry => new { entry.SourceId, entry.Key }).IsUnique();
            entity.Property(entry => entry.SourceId).HasMaxLength(128);
            entity.Property(entry => entry.Key).HasMaxLength(128);
            entity.Property(entry => entry.Value).HasMaxLength(4096);
        });

        modelBuilder.Entity<ScopedStorageRecordDB>(entity =>
        {
            entity.ToTable("ScopedStorageRecords");
            entity.HasIndex(record => new
            {
                record.SourceId,
                record.StorageName,
                record.RecordKey,
            }).IsUnique();
            entity.Property(record => record.SourceId).HasMaxLength(128);
            entity.Property(record => record.StorageName).HasMaxLength(128);
            entity.Property(record => record.RecordKey).HasMaxLength(256);
        });

        modelBuilder.Entity<ScopedStorageIndexEntryDB>(entity =>
        {
            entity.ToTable("ScopedStorageIndexes");
            entity.HasIndex(index => new
            {
                index.SourceId,
                index.StorageName,
                index.IndexName,
                index.StringValue,
                index.RecordKey,
            });
            entity.HasIndex(index => new
            {
                index.SourceId,
                index.StorageName,
                index.IndexName,
                index.NumberValue,
                index.RecordKey,
            });
            entity.HasIndex(index => new
            {
                index.SourceId,
                index.StorageName,
                index.IndexName,
                index.DateTimeValue,
                index.RecordKey,
            });
            entity.HasIndex(index => new
            {
                index.SourceId,
                index.StorageName,
                index.IndexName,
                index.BoolValue,
                index.RecordKey,
            });
            entity.HasIndex(index => new
            {
                index.SourceId,
                index.StorageName,
                index.RecordKey,
            });
            entity.Property(index => index.SourceId).HasMaxLength(128);
            entity.Property(index => index.StorageName).HasMaxLength(128);
            entity.Property(index => index.IndexName).HasMaxLength(128);
            entity.Property(index => index.RecordKey).HasMaxLength(256);
            entity.Property(index => index.StringValue).HasMaxLength(1024);
        });

        ConfigureForProvider(modelBuilder);
    }

    private void ConfigureForProvider(ModelBuilder modelBuilder)
    {
        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            return;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(
                        value => value.ToUnixTimeMilliseconds(),
                        value => DateTimeOffset.FromUnixTimeMilliseconds(value)));
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new ValueConverter<DateTimeOffset?, long?>(
                        value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
                        value => value.HasValue
                            ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value)
                            : null));
                }
            }
        }
    }

    public override async Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_persistenceActionRunnerAccessor is not null)
            return await SaveChangesThroughKernelAsync(cancellationToken);

        return await SaveChangesCoreAsync(cancellationToken);
    }

    private async Task<int> SaveChangesCoreAsync(CancellationToken cancellationToken)
    {
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

        return await base.SaveChangesAsync(cancellationToken);
    }
}
