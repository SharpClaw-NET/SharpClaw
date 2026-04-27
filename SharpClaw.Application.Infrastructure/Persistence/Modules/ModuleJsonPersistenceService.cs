using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;

namespace SharpClaw.Infrastructure.Persistence.Modules;

public sealed class ModuleJsonPersistenceService(
    IPersistenceFileSystem fs,
    JsonFileOptions options,
    EncryptionOptions encryptionOptions,
    IModuleDbContextFactory moduleDbContextFactory,
    RuntimeModuleDbContextRegistry registry,
    ILogger<ModuleJsonPersistenceService> logger,
    DirectoryLockManager? directoryLockManager = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task LoadRegisteredModulesAsync(CancellationToken ct = default)
    {
        foreach (var registration in registry.GetAll())
            await LoadModuleAsync(registration, ct);
    }

    public async Task LoadModuleAsync(RuntimeModuleDbContextRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        await using var context = (DbContext)moduleDbContextFactory.CreateDbContext(registration.DbContextType);
        var navigations = GetNavigations(context);

        // First pass: load all regular (non-join) entities so FK targets exist.
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.HasSharedClrType)
                continue;

            var clrType = entityType.ClrType;
            var entityDir = GetEntityDirectory(registration.ModuleId, clrType);
            await LoadEntityDirectoryAsync(context, clrType, navigations.GetValueOrDefault(clrType), entityDir, ct);
        }

        await context.SaveChangesAsync(ct);

        // Second pass: load join tables after all FK targets are committed.
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (!entityType.HasSharedClrType)
                continue;

            await LoadJoinTableAsync(context, registration.ModuleId, entityType, ct);
        }

        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();
    }

    internal async Task FlushChangesAsync(
        DbContext context,
        RuntimeModuleDbContextRegistration registration,
        IReadOnlyList<(Type ClrType, Guid Id, EntityState State)> entityChanges,
        IReadOnlySet<string> joinTableChanges,
        CancellationToken ct = default)
    {
        if (entityChanges.Count == 0 && joinTableChanges.Count == 0)
            return;

        fs.CreateDirectory(options.DataDirectory);

        // Phase A′: Write transaction manifest BEFORE any entity files.
        var twoPhase = new TwoPhaseCommit(fs, options.FsyncOnWrite);
        var markerDir = GetModuleDirectory(registration.ModuleId);
        fs.CreateDirectory(markerDir);

        // Phase J: Collect per-directory checksum changes.
        Dictionary<string, List<(string FileName, ReadOnlyMemory<byte> Data, bool Deleted)>>? checksumChanges =
            options.EnableChecksums ? new(StringComparer.OrdinalIgnoreCase) : null;

        var navigations = GetNavigations(context);

        // Group entity changes by directory so each directory lock is acquired once.
        var byDir = entityChanges.GroupBy(e => GetEntityDirectory(registration.ModuleId, e.ClrType));
        foreach (var group in byDir)
        {
            var dirPath = group.Key;
            using var _ = directoryLockManager is not null
                ? await directoryLockManager.AcquireAsync(dirPath, ct)
                : null;

            fs.CreateDirectory(dirPath);

            foreach (var (clrType, id, state) in group)
            {
                var filePath = fs.CombinePath(dirPath, $"{id}.json");
                var fileName = $"{id}.json";

                try
                {
                    if (state == EntityState.Deleted)
                    {
                        twoPhase.StageDelete(filePath);
                        checksumChanges?.GetOrAdd(dirPath).Add((fileName, ReadOnlyMemory<byte>.Empty, Deleted: true));
                    }
                    else
                    {
                        var entity = context.Find(clrType, id);
                        if (entity is null)
                            continue;

                        var bytes = SerializeEntityToBytes(entity, clrType, navigations.GetValueOrDefault(clrType));
                        var prepared = JsonFileEncryption.PrepareBytes(bytes, encryptionOptions.Key, options.EncryptAtRest);
                        await twoPhase.StageAsync(filePath, prepared, ct);
                        checksumChanges?.GetOrAdd(dirPath).Add((fileName, prepared, Deleted: false));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to stage module entity {Type} {Id} to {Path}", clrType.Name, id, filePath);
                }
            }
        }

        // Stage join table writes.
        if (joinTableChanges.Count > 0)
        {
            foreach (var entityType in context.Model.GetEntityTypes())
            {
                if (!entityType.HasSharedClrType)
                    continue;
                if (!joinTableChanges.Contains(entityType.Name))
                    continue;

                await StageJoinTableAsync(twoPhase, context, registration.ModuleId, entityType, checksumChanges, ct);
            }
        }

        // Commit: write marker → rename all .tmp → final → delete marker.
        await twoPhase.CommitAsync(markerDir, ct);

        // Phase J: Batch-update checksum manifests after successful commit.
        if (checksumChanges is { Count: > 0 })
        {
            foreach (var (dir, changes) in checksumChanges)
            {
                try
                {
                    await ChecksumManifest.UpdateChecksumsAsync(
                        fs, dir, changes, encryptionOptions.Key, options.FsyncOnWrite, logger, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to update checksum manifest for module dir {Dir}", dir);
                }
            }
        }

        // Phase H: Append entity change events to the event log.
        if (options.EnableEventLog && entityChanges.Count > 0)
        {
            try
            {
                var eventLog = new EventLog(fs, options, encryptionOptions.Key, logger);
                await eventLog.AppendAsync(registration.ModuleId, entityChanges, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to append module events to event log for module {ModuleId}", registration.ModuleId);
            }
        }
    }

    // ── Join table helpers ──────────────────────────────────────────────────

    private async Task LoadJoinTableAsync(
        DbContext context,
        string moduleId,
        IEntityType entityType,
        CancellationToken ct)
    {
        var tableName = entityType.Name;
        var tableDir = GetJoinTableDirectory(moduleId, tableName);
        var filePath = fs.CombinePath(tableDir, "_rows.json");

        if (!fs.FileExists(filePath))
            return;

        try
        {
            var json = await JsonFileEncryption.ReadJsonAsync(fs, filePath, encryptionOptions.Key, ct);
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, Guid>>>(json, JsonOptions);
            if (rows is null || rows.Count == 0)
                return;

            var fkPropertyNames = entityType.GetProperties()
                .Where(p => p.IsForeignKey())
                .Select(p => p.Name)
                .ToList();

            var dbSet = context.Set<Dictionary<string, object>>(tableName);
            foreach (var row in rows)
            {
                var entity = new Dictionary<string, object>(fkPropertyNames.Count);
                foreach (var fkName in fkPropertyNames)
                {
                    if (row.TryGetValue(fkName, out var guid))
                        entity[fkName] = guid;
                }

                if (entity.Count == fkPropertyNames.Count)
                    dbSet.Add(entity);
            }

            logger.LogDebug("Loaded {Count} module {Table} join rows from {Path}", rows.Count, tableName, filePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load module join table {Table} from {Path}", tableName, filePath);
        }
    }

    private async Task StageJoinTableAsync(
        TwoPhaseCommit twoPhase,
        DbContext context,
        string moduleId,
        IEntityType entityType,
        Dictionary<string, List<(string FileName, ReadOnlyMemory<byte> Data, bool Deleted)>>? checksumChanges,
        CancellationToken ct)
    {
        var tableName = entityType.Name;
        var tableDir = GetJoinTableDirectory(moduleId, tableName);

        using var _ = directoryLockManager is not null
            ? await directoryLockManager.AcquireAsync(tableDir, ct)
            : null;

        fs.CreateDirectory(tableDir);

        try
        {
            var fkProperties = entityType.GetProperties()
                .Where(p => p.IsForeignKey())
                .ToList();

            var dbSet = context.Set<Dictionary<string, object>>(tableName);
            var rows = await dbSet.ToListAsync(ct);

            var filePath = fs.CombinePath(tableDir, "_rows.json");

            if (rows.Count > 0)
            {
                var rowList = new List<Dictionary<string, Guid>>(rows.Count);
                foreach (var row in rows)
                {
                    var dict = new Dictionary<string, Guid>(fkProperties.Count);
                    foreach (var fk in fkProperties)
                    {
                        if (row.TryGetValue(fk.Name, out var val) && val is Guid g)
                            dict[fk.Name] = g;
                    }
                    rowList.Add(dict);
                }

                var json = JsonSerializer.Serialize(rowList, JsonOptions);
                var prepared = JsonFileEncryption.PrepareJson(json, encryptionOptions.Key, options.EncryptAtRest);
                await twoPhase.StageAsync(filePath, prepared, ct);
                checksumChanges?.GetOrAdd(tableDir).Add(("_rows.json", prepared, Deleted: false));
            }
            else
            {
                twoPhase.StageDelete(filePath);
                checksumChanges?.GetOrAdd(tableDir).Add(("_rows.json", ReadOnlyMemory<byte>.Empty, Deleted: true));
            }

            logger.LogDebug("Staged {Count} module {Table} join rows", rows.Count, tableName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stage module join table {Table}", tableName);
        }
    }

    // ── Entity directory helpers ────────────────────────────────────────────

    private async Task<int> LoadEntityDirectoryAsync(
        DbContext context,
        Type clrType,
        List<System.Reflection.PropertyInfo>? navProps,
        string entityDir,
        CancellationToken ct)
    {
        if (!fs.DirectoryExists(entityDir))
            return 0;

        var files = fs.GetFiles(entityDir, "*.json")
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .ToArray();

        if (files.Length == 0)
            return 0;

        var loaded = 0;
        foreach (var file in files)
        {
            try
            {
                var json = await JsonFileEncryption.ReadJsonAsync(fs, file, encryptionOptions.Key, ct);
                var entity = JsonSerializer.Deserialize(json, clrType, JsonOptions);
                if (entity is null)
                    continue;

                if (navProps is not null)
                {
                    foreach (var prop in navProps)
                        prop.SetValue(entity, null);
                }

                // Skip entities already present in the InMemory store (e.g. re-enabling a
                // previously disabled module that was already loaded at startup).
                if (entity is BaseEntity be && context.Find(clrType, be.Id) is not null)
                    continue;

                context.Add(entity);
                loaded++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load module entity {Type} from {Path}", clrType.Name, file);
            }
        }

        logger.LogDebug("Loaded {Count} module {Type} entities from {Path}", loaded, clrType.Name, entityDir);
        return loaded;
    }

    private string GetModuleDirectory(string moduleId) =>
        fs.CombinePath(options.DataDirectory, "modules", moduleId);

    private string GetEntityDirectory(string moduleId, Type entityType) =>
        fs.CombinePath(options.DataDirectory, "modules", moduleId, entityType.Name);

    private string GetJoinTableDirectory(string moduleId, string tableName) =>
        fs.CombinePath(options.DataDirectory, "modules", moduleId, tableName);

    private static Dictionary<Type, List<System.Reflection.PropertyInfo>> GetNavigations(DbContext context)
    {
        return context.Model.GetEntityTypes()
            .Where(e => !e.HasSharedClrType)
            .GroupBy(e => e.ClrType)
            .ToDictionary(
                g => g.Key,
                g => g.First()
                    .GetNavigations()
                    .Select(n => n.PropertyInfo)
                    .Concat(g.First().GetSkipNavigations().Select(n => n.PropertyInfo))
                    .Where(p => p is not null)
                    .Select(p => p!)
                    .ToList());
    }

    private static byte[] SerializeEntityToBytes(
        object entity,
        Type clrType,
        List<System.Reflection.PropertyInfo>? navProps)
    {
        if (navProps is null || navProps.Count == 0)
            return JsonSerializer.SerializeToUtf8Bytes(entity, clrType, JsonOptions);

        var saved = new object?[navProps.Count];
        for (var i = 0; i < navProps.Count; i++)
        {
            saved[i] = navProps[i].GetValue(entity);
            navProps[i].SetValue(entity, null);
        }

        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(entity, clrType, JsonOptions);
        }
        finally
        {
            for (var i = 0; i < navProps.Count; i++)
                navProps[i].SetValue(entity, saved[i]);
        }
    }
}

public sealed class ModuleJsonSaveChangesInterceptor(
    RuntimeModuleDbContextRegistry registry,
    ModuleJsonPersistenceService moduleJsonPersistence,
    ModuleDbContextOptions moduleOptions) : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<DbContextId, (IReadOnlyList<(Type ClrType, Guid Id, EntityState State)> EntityChanges, IReadOnlySet<string> JoinTableChanges)> _pendingChanges = [];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (moduleOptions.StorageMode != StorageMode.JsonFile || eventData.Context is null)
            return result;

        SetAuditFields(eventData.Context);
        _pendingChanges[eventData.Context.ContextId] = (
            GetEntityChanges(eventData.Context),
            GetJoinTableChanges(eventData.Context));
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (moduleOptions.StorageMode != StorageMode.JsonFile || eventData.Context is null)
            return result;

        var registration = registry.GetRegistration(eventData.Context.GetType());
        if (registration is null)
            return result;

        if (!_pendingChanges.TryRemove(eventData.Context.ContextId, out var pending))
            return result;

        await moduleJsonPersistence.FlushChangesAsync(
            eventData.Context, registration, pending.EntityChanges, pending.JoinTableChanges, cancellationToken);

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private static void SetAuditFields(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<BaseEntity>())
        {
            var now = DateTimeOffset.UtcNow;
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.Id == Guid.Empty)
                    entry.Entity.Id = Guid.NewGuid();
                if (entry.Entity.CreatedAt == default)
                    entry.Entity.CreatedAt = now;
                if (entry.Entity.UpdatedAt == default)
                    entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }

    private static IReadOnlyList<(Type ClrType, Guid Id, EntityState State)> GetEntityChanges(DbContext context)
    {
        var changes = new List<(Type ClrType, Guid Id, EntityState State)>();
        foreach (var entry in context.ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                changes.Add((entry.Entity.GetType(), entry.Entity.Id, entry.State));
        }

        return changes;
    }

    private static IReadOnlySet<string> GetJoinTableChanges(DbContext context)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var entityType = context.Model.FindEntityType(entry.Metadata.Name);
            if (entityType is { HasSharedClrType: true })
                names.Add(entry.Metadata.Name);
        }

        return names;
    }
}

file static class ModuleDictionaryListExtensions
{
    internal static List<TValue> GetOrAdd<TKey, TValue>(
        this Dictionary<TKey, List<TValue>> dict, TKey key) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = [];
            dict[key] = list;
        }
        return list;
    }
}
