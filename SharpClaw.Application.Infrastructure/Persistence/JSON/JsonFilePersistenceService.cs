using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Infrastructure.Persistence.JSON;

/// <summary>
/// Handles loading entities from and saving entities to JSON files on disk,
/// keeping the InMemory EFC database in sync with the file system.
/// </summary>
public sealed class JsonFilePersistenceService(
    IPersistenceFileSystem fs,
    SharpClawDbContext context,
    JsonFileOptions options,
    EncryptionOptions encryptionOptions,
    ILogger<JsonFilePersistenceService> logger,
    DirectoryLockManager? directoryLockManager = null,
    TransactionQueue? transactionQueue = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter(), new NullableGuidConverter() }
    };

    /// <summary>
    /// Lazily-built lookup: CLR type → list of navigation PropertyInfos.
    /// Used to strip navigation properties before serialization so each
    /// JSON file contains only scalar/FK data, not the entire object graph.
    /// </summary>
    private Dictionary<Type, List<System.Reflection.PropertyInfo>>? _navigations;

    private Dictionary<Type, List<System.Reflection.PropertyInfo>> GetNavigations()
    {
        return _navigations ??= context.Model.GetEntityTypes()
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

    /// <summary>
    /// Loads all per-entity JSON files from the data directory into the InMemory database.
    /// Each entity type has its own sub-folder under the data directory, and each entity
    /// is stored as an individual <c>{Id}.json</c> file.
    /// Many-to-many join tables are loaded from a single <c>_rows.json</c> array file.
    /// Call once at startup after the DbContext is configured.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        fs.CreateDirectory(options.DataDirectory);

        // Phase D: Clean up orphan .tmp files left by interrupted writes.
        CleanupAllTempFiles();

        var navigations = GetNavigations();

        // First pass: load all regular (non-join) entities so FK targets exist.
        // File I/O is parallelised per entity type to reduce wall-clock time.
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.HasSharedClrType)
                continue;

            var clrType = entityType.ClrType;

            if (options.ColdEntityTypes.Contains(clrType))
                continue;
            var entityDir = GetEntityDirectory(clrType);

            if (!fs.DirectoryExists(entityDir))
                continue;

            var navProps = navigations.GetValueOrDefault(clrType);
            var files = fs.GetFiles(entityDir, "*.json");

            if (files.Length == 0)
                continue;

            // Read all files for this entity type in parallel.
            var readTasks = files.Select(async file =>
            {
                try
                {
                    var json = await ReadJsonAsync(file, ct);
                    return (Json: json, File: file, Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Json: (string?)null, File: file, Error: ex);
                }
            });

            var results = await Task.WhenAll(readTasks);

            foreach (var result in results)
            {
                if (result.Error is not null)
                {
                    logger.LogError(result.Error, "Failed to read {Type} from {Path}", clrType.Name, result.File);
                    continue;
                }

                try
                {
                    // RGAP-9: detect files written by a newer version of the software.
                    // This guards against silent data corruption if the binary is rolled back.
                    var fileVersion = JsonSchemaVersion.ReadFrom(result.Json!);
                    if (fileVersion > JsonSchemaVersion.Current)
                    {
                        logger.LogWarning(
                            "Entity file {Path} has $schemaVersion {FileVersion} which is newer than " +
                            "the current schema version {CurrentVersion}. " +
                            "This file was written by a newer build — data may be lost on re-flush.",
                            result.File, fileVersion, JsonSchemaVersion.Current);
                    }

                    var entity = JsonSerializer.Deserialize(result.Json!, clrType, JsonOptions);

                    if (entity is null)
                        continue;

                    // Clear navigation properties so EF only tracks this entity
                    // by its foreign-key columns, not the nested object graph.
                    if (navProps is not null)
                    {
                        foreach (var prop in navProps)
                            prop!.SetValue(entity, null);
                    }

                    context.Add(entity);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load {Type} from {Path}", clrType.Name, result.File);
                }
            }

            logger.LogDebug("Loaded {Count} {Type} from {Path}", files.Length, clrType.Name, entityDir);
        }

        context.SuppressJsonFlush = true;
        try
        {
            await context.SaveChangesAsync(ct);

            // Second pass: load many-to-many join tables after all entities
            // are committed so the FK references resolve correctly.
            foreach (var entityType in context.Model.GetEntityTypes())
            {
                if (!entityType.HasSharedClrType)
                    continue;

                await LoadJoinTableAsync(entityType, ct);
            }

            await context.SaveChangesAsync(ct);
        }
        finally
        {
            context.SuppressJsonFlush = false;
        }

        DetachAll();
    }

    /// <summary>
    /// Checks whether the on-disk JSON files appear to contain bloated
    /// navigation-property graphs (a legacy serialization issue) and, if
    /// so, performs a full <see cref="FlushAsync"/> to recompact them.
    /// A sentinel file (<c>.compacted</c>) is written after a successful
    /// recompact so subsequent startups skip the check.
    /// </summary>
    public async Task RecompactIfNeededAsync(CancellationToken ct = default)
    {
        var sentinelPath = fs.CombinePath(options.DataDirectory, ".compacted");
        if (fs.FileExists(sentinelPath))
            return;

        logger.LogInformation("Recompacting JSON data files (one-time migration)...");
        await FlushAsync(ct);

        await fs.WriteAllTextAsync(sentinelPath, DateTimeOffset.UtcNow.ToString("O"), ct);
        logger.LogInformation("JSON data files recompacted successfully.");
    }

    /// <summary>
    /// Saves all tracked entity types from the InMemory database to individual JSON files.
    /// Each entity is written as <c>{DataDirectory}/{EntityType}/{Id}.json</c>.
    /// Orphan files for deleted entities are removed.
    /// Many-to-many join tables (shared-type entities) are written as a single
    /// <c>_rows.json</c> file containing an array of FK-pair objects.
    /// <para>
    /// This full-scan flush should only be used for startup migrations or
    /// manual bulk operations.  Normal per-save persistence uses the
    /// incremental <see cref="FlushChangesAsync"/> to avoid race conditions
    /// between concurrent scopes.
    /// </para>
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        fs.CreateDirectory(options.DataDirectory);

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.HasSharedClrType)
            {
                await FlushJoinTableAsync(entityType, ct);
                continue;
            }

            var clrType = entityType.ClrType;
            var entityDir = GetEntityDirectory(clrType);

            using var _ = directoryLockManager is not null
                ? await directoryLockManager.AcquireAsync(entityDir, ct)
                : null;

            fs.CreateDirectory(entityDir);

            try
            {
                var queryable = (IQueryable<object>)context
                    .GetType()
                    .GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                    .MakeGenericMethod(clrType)
                    .Invoke(context, null)!;

                var entities = await queryable.ToListAsync(ct);
                var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var navProps = GetNavigations().GetValueOrDefault(clrType);

                foreach (var entity in entities)
                {
                    var id = ((BaseEntity)entity).Id;
                    activeIds.Add($"{id}.json");

                    var filePath = fs.CombinePath(entityDir, $"{id}.json");
                    var json = SerializeEntityToBytes(entity, clrType, navProps);
                    await WriteBytesAsync(filePath, json, ct);
                }

                // Remove orphan files for entities that no longer exist
                foreach (var file in fs.GetFiles(entityDir, "*.json"))
                {
                    if (!activeIds.Contains(fs.GetFileName(file)))
                        fs.DeleteFile(file);
                }

                logger.LogDebug("Flushed {Count} {Type} to {Path}", entities.Count, clrType.Name, entityDir);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to flush {Type} to {Path}", clrType.Name, entityDir);
            }
        }
    }

    /// <summary>
    /// Incrementally persists only the entities that changed in the most
    /// recent <see cref="SharpClawDbContext.SaveChangesAsync"/> call.
    /// <list type="bullet">
    ///   <item><b>Added / Modified</b> — entity is re-read via
    ///     <see cref="DbContext.Find(Type, object?[])"/> and written to
    ///     its <c>{Id}.json</c> file.</item>
    ///   <item><b>Deleted</b> — the corresponding <c>{Id}.json</c> file
    ///     is removed from disk.</item>
    ///   <item><b>Join tables</b> — the entire <c>_rows.json</c> for each
    ///     affected join table is rewritten.</item>
    /// </list>
    /// <para>
    /// Because each invocation touches only its own changed files (rather
    /// than scanning every entity directory for orphans), concurrent
    /// flushes from different DI scopes can no longer delete each other's
    /// files.
    /// </para>
    /// </summary>
    public async Task FlushChangesAsync(
        IReadOnlyList<(Type ClrType, Guid Id, EntityState State)> entityChanges,
        IReadOnlySet<string> joinTableChanges,
        CancellationToken ct = default)
    {
        // Phase A′: Write transaction manifest BEFORE any entity files (RGAP-10).
        string? manifestPath = null;
        if (transactionQueue is not null)
        {
            manifestPath = await transactionQueue.EnqueueAsync(entityChanges, joinTableChanges, ct);
        }

        try
        {
            await FlushChangesInternalAsync(entityChanges, joinTableChanges, ct);
        }
        catch
        {
            // Manifest stays in pending/ for crash recovery replay.
            throw;
        }

        // Success — dequeue the manifest.
        if (manifestPath is not null)
            transactionQueue!.Dequeue(manifestPath);
    }

    /// <summary>
    /// The actual file-writing logic, extracted so it can be called both
    /// from normal <see cref="FlushChangesAsync"/> and from transaction replay.
    /// </summary>
    internal async Task FlushChangesInternalAsync(
        IReadOnlyList<(Type ClrType, Guid Id, EntityState State)> entityChanges,
        IReadOnlySet<string> joinTableChanges,
        CancellationToken ct = default)
    {
        fs.CreateDirectory(options.DataDirectory);

        // Phase C: Two-phase commit — stage all writes, then atomically commit.
        var twoPhase = new TwoPhaseCommit(fs, options.FsyncOnWrite);

        // Phase J: Collect per-directory checksum changes to batch-update after commit.
        Dictionary<string, List<(string FileName, ReadOnlyMemory<byte> Data, bool Deleted)>>? checksumChanges =
            options.EnableChecksums ? new(StringComparer.OrdinalIgnoreCase) : null;

        // Group entity changes by directory so each directory lock is acquired once.
        var byDir = entityChanges.GroupBy(e => GetEntityDirectory(e.ClrType));

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

                        // Remove from on-disk index for cold entity types
                        if (options.ColdEntityTypes.Contains(clrType))
                        {
                            await ColdEntityIndex.UpdateIndexAsync(
                                fs, dirPath, clrType.Name, id, entity: null,
                                deleted: true, logger, ct);
                        }
                    }
                    else
                    {
                        var entity = context.Find(clrType, id);
                        if (entity is not null)
                        {
                            var navProps = GetNavigations().GetValueOrDefault(clrType);
                            var bytes = SerializeEntityToBytes(entity, clrType, navProps);
                            var prepared = JsonFileEncryption.PrepareBytes(
                                bytes, encryptionOptions.Key, options.EncryptAtRest);
                            await twoPhase.StageAsync(filePath, prepared, ct);
                            checksumChanges?.GetOrAdd(dirPath).Add((fileName, prepared, Deleted: false));

                            // Update on-disk index for cold entity types
                            if (options.ColdEntityTypes.Contains(clrType))
                            {
                                await ColdEntityIndex.UpdateIndexAsync(
                                    fs, dirPath, clrType.Name, id, entity,
                                    deleted: false, logger, ct);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to stage {Type} {Id} to {Path}",
                        clrType.Name, id, filePath);
                }
            }
        }

        // Stage join table writes inside the two-phase envelope.
        if (joinTableChanges.Count > 0)
        {
            foreach (var entityType in context.Model.GetEntityTypes())
            {
                if (!entityType.HasSharedClrType)
                    continue;
                if (!joinTableChanges.Contains(entityType.Name))
                    continue;

                await StageJoinTableAsync(twoPhase, entityType, checksumChanges, ct);
            }
        }

        // Commit: write marker → rename all .tmp → final → delete marker.
        await twoPhase.CommitAsync(options.DataDirectory, ct);

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
                    logger.LogWarning(ex, "Failed to update checksum manifest for {Dir}", dir);
                }
            }
        }

        // Phase H: Append entity change events to the event log after commit.
        if (options.EnableEventLog)
        {
            try
            {
                var eventLog = new EventLog(fs, options, encryptionOptions.Key, logger);
                await eventLog.AppendAsync(entityChanges, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to append events to event log");
            }
        }

        // Detach flushed entities from the change tracker to release
        // property-snapshot memory.
        foreach (var (clrType, id, state) in entityChanges)
        {
            if (state == EntityState.Deleted)
                continue;
            var entry = context.ChangeTracker.Entries()
                .FirstOrDefault(e => e.Entity is BaseEntity be && be.Id == id);
            if (entry is not null)
                entry.State = EntityState.Detached;
        }
    }

    private string GetEntityDirectory(Type entityType)
        => fs.CombinePath(options.DataDirectory, entityType.Name);

    private string GetJoinTableDirectory(string tableName)
        => fs.CombinePath(options.DataDirectory, tableName);

    /// <summary>
    /// Flushes a shared-type (many-to-many join table) entity to a single
    /// <c>_rows.json</c> file. Each row is serialised as a dictionary of
    /// FK property names to their <see cref="Guid"/> values.
    /// </summary>
    private async Task FlushJoinTableAsync(IEntityType entityType, CancellationToken ct)
    {
        var tableName = entityType.Name;
        var tableDir = GetJoinTableDirectory(tableName);

        // RGAP-11: Acquire per-join-table directory lock.
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
                await WriteJsonAsync(filePath, json, ct);
            }
            else if (fs.FileExists(filePath))
            {
                fs.DeleteFile(filePath);
            }

            logger.LogDebug("Flushed {Count} {Table} join rows", rows.Count, tableName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to flush join table {Table}", tableName);
        }
    }

    /// <summary>
    /// Stages a join table write into the two-phase commit envelope.
    /// </summary>
    private async Task StageJoinTableAsync(
        TwoPhaseCommit twoPhase,
        IEntityType entityType,
        Dictionary<string, List<(string FileName, ReadOnlyMemory<byte> Data, bool Deleted)>>? checksumChanges,
        CancellationToken ct)
    {
        var tableName = entityType.Name;
        var tableDir = GetJoinTableDirectory(tableName);

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
                var prepared = JsonFileEncryption.PrepareJson(
                    json, encryptionOptions.Key, options.EncryptAtRest);
                await twoPhase.StageAsync(filePath, prepared, ct);
                checksumChanges?.GetOrAdd(tableDir).Add(("_rows.json", prepared, Deleted: false));
            }
            else
            {
                twoPhase.StageDelete(filePath);
                checksumChanges?.GetOrAdd(tableDir).Add(("_rows.json", ReadOnlyMemory<byte>.Empty, Deleted: true));
            }

            logger.LogDebug("Staged {Count} {Table} join rows", rows.Count, tableName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stage join table {Table}", tableName);
        }
    }

    /// <summary>
    /// Loads a shared-type (many-to-many join table) entity from its
    /// <c>_rows.json</c> file and adds each row to the context.
    /// </summary>
    private async Task LoadJoinTableAsync(IEntityType entityType, CancellationToken ct)
    {
        var tableName = entityType.Name;
        var tableDir = GetJoinTableDirectory(tableName);
        var filePath = fs.CombinePath(tableDir, "_rows.json");

        if (!fs.FileExists(filePath))
            return;

        try
        {
            var json = await ReadJsonAsync(filePath, ct);
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, Guid>>>(json, JsonOptions);
            if (rows is null || rows.Count == 0)
                return;

            var fkPropertyNames = entityType.GetProperties()
                .Where(p => p.IsForeignKey())
                .Select(p => p.Name)
                .ToList();

            var dbSet = context.Set<Dictionary<string, object>>(entityType.Name);

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

            logger.LogDebug("Loaded {Count} {Table} join rows", rows.Count, tableName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load join table {Table} from {Path}", tableName, filePath);
        }
    }

    /// <summary>
    /// Serializes an entity to JSON, temporarily nulling navigation properties
    /// so only scalar and FK columns are persisted.  This prevents the JSON
    /// files from bloating with the entire related object graph (e.g. every
    /// ModelDB file embedding the full Provider with all its sibling models).
    /// </summary>
    private static string SerializeEntity(
        object entity, Type clrType,
        List<System.Reflection.PropertyInfo>? navProps)
    {
        if (navProps is null || navProps.Count == 0)
            return JsonSerializer.Serialize(entity, clrType, JsonOptions);

        // Snapshot current navigation values, null them, serialize, restore.
        var saved = new object?[navProps.Count];
        for (var i = 0; i < navProps.Count; i++)
        {
            saved[i] = navProps[i]!.GetValue(entity);
            navProps[i]!.SetValue(entity, null);
        }

        try
        {
            return JsonSerializer.Serialize(entity, clrType, JsonOptions);
        }
        finally
        {
            for (var i = 0; i < navProps.Count; i++)
                navProps[i]!.SetValue(entity, saved[i]);
        }
    }

    /// <summary>
    /// Serializes an entity directly to UTF-8 bytes using a pooled
    /// <see cref="ArrayBufferWriter{T}"/>, avoiding intermediate
    /// <c>byte[]</c> allocations. Navigation properties are temporarily
    /// nulled using the same snapshot/restore pattern.
    /// <para>
    /// A <c>$schemaVersion</c> field is injected into the root JSON object
    /// so future migration pipelines can detect and upgrade older files
    /// (RGAP-9 groundwork — no migration logic runs yet).
    /// </para>
    /// </summary>
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_bufferWriter;

    private static byte[] SerializeEntityToBytes(
        object entity, Type clrType,
        List<System.Reflection.PropertyInfo>? navProps)
    {
        var writer = t_bufferWriter ??= new ArrayBufferWriter<byte>(4096);
        writer.ResetWrittenCount();

        if (navProps is null || navProps.Count == 0)
        {
            using var jsonWriter = new Utf8JsonWriter(writer);
            JsonSerializer.Serialize(jsonWriter, entity, clrType, JsonOptions);
            return JsonSchemaVersion.Inject(writer.WrittenSpan);
        }

        var saved = new object?[navProps.Count];
        for (var i = 0; i < navProps.Count; i++)
        {
            saved[i] = navProps[i]!.GetValue(entity);
            navProps[i]!.SetValue(entity, null);
        }

        try
        {
            using var jsonWriter = new Utf8JsonWriter(writer);
            JsonSerializer.Serialize(jsonWriter, entity, clrType, JsonOptions);
            return JsonSchemaVersion.Inject(writer.WrittenSpan);
        }
        finally
        {
            for (var i = 0; i < navProps.Count; i++)
                navProps[i]!.SetValue(entity, saved[i]);
        }
    }

    /// <summary>
    /// One-time migration: re-encrypts all JSON data files on disk.
    /// A <c>.encrypted</c> sentinel prevents repeat runs.
    /// </summary>
    public async Task EncryptIfNeededAsync(CancellationToken ct = default)
    {
        if (!options.EncryptAtRest)
            return;

        var sentinelPath = fs.CombinePath(options.DataDirectory, ".encrypted");
        if (fs.FileExists(sentinelPath))
            return;

        logger.LogInformation("Encrypting JSON data files (one-time migration)...");
        await FlushAsync(ct);
        await fs.WriteAllTextAsync(sentinelPath, DateTimeOffset.UtcNow.ToString("O"), ct);
        logger.LogInformation("JSON data files encrypted successfully.");
    }

    private Task WriteJsonAsync(string path, string json, CancellationToken ct)
        => JsonFileEncryption.WriteJsonAsync(fs, path, json, encryptionOptions.Key, options.EncryptAtRest, options.FsyncOnWrite, ct);

    private Task WriteBytesAsync(string path, byte[] utf8Json, CancellationToken ct)
        => JsonFileEncryption.WriteBytesAsync(fs, path, utf8Json, encryptionOptions.Key, options.EncryptAtRest, options.FsyncOnWrite, ct);

    private Task<string> ReadJsonAsync(string path, CancellationToken ct)
        => JsonFileEncryption.ReadJsonAsync(fs, path, encryptionOptions.Key, ct);

    /// <summary>
    /// Replays a transaction manifest by re-flushing the described entity changes.
    /// During replay the entities may or may not exist in the InMemory store
    /// (they were already committed by <c>base.SaveChangesAsync</c> before crash).
    /// For Added/Modified states, we attempt to find and re-serialize the entity;
    /// if the entity is not found (e.g., the InMemory store was lost on restart),
    /// the manifest is left for the next <see cref="LoadAsync"/> cycle to pick up
    /// naturally since <see cref="LoadAsync"/> reads from the disk files that may
    /// already be partially written.
    /// </summary>
    internal async Task ReplayManifestAsync(TransactionManifest manifest, CancellationToken ct)
    {
        var entityChanges = new List<(Type ClrType, Guid Id, EntityState State)>();

        foreach (var entry in manifest.EntityChanges)
        {
            var clrType = ResolveEntityType(entry.EntityType);
            if (clrType is null)
            {
                logger.LogWarning(
                    "Unknown entity type {TypeName} in manifest (seq {Sequence}), skipping",
                    entry.EntityType, manifest.Sequence);
                continue;
            }

            entityChanges.Add((clrType, entry.Id, entry.State));
        }

        var joinTableChanges = new HashSet<string>(manifest.JoinTableChanges);

        await FlushChangesInternalAsync(entityChanges, joinTableChanges, ct);
    }

    /// <summary>
    /// Resolves an entity CLR type by its <see cref="Type.Name"/>.
    /// Searches the EF model for a matching entity type.
    /// </summary>
    private Type? ResolveEntityType(string typeName)
    {
        return context.Model.GetEntityTypes()
            .Where(e => !e.HasSharedClrType)
            .Select(e => e.ClrType)
            .FirstOrDefault(t => t.Name == typeName);
    }

    private void DetachAll()
    {
        context.ChangeTracker.Clear();
    }

    /// <summary>
    /// Handles JSON <c>null</c> for non-nullable <see cref="Guid"/> properties
    /// by substituting <see cref="Guid.Empty"/>. This prevents deserialization
    /// failures when old data files contain <c>null</c> for foreign-key columns
    /// that were later made non-nullable.
    /// </summary>
    private sealed class NullableGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? Guid.Empty : reader.GetGuid();

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    /// <summary>
    /// Cleans up orphan <c>.tmp</c> files from all subdirectories under
    /// <see cref="JsonFileOptions.DataDirectory"/>. Called on startup before
    /// loading entities.
    /// </summary>
    private void CleanupAllTempFiles()
    {
        if (!fs.DirectoryExists(options.DataDirectory))
            return;

        ColdEntityIndex.CleanupTempFiles(fs, options.DataDirectory, logger);
        foreach (var subDir in fs.GetDirectories(options.DataDirectory))
            ColdEntityIndex.CleanupTempFiles(fs, subDir, logger);
    }

    /// <summary>
    /// Rebuilds cold entity indexes in parallel across entity types.
    /// Each entity type directory is rebuilt concurrently via <see cref="Task.WhenAll"/>.
    /// </summary>
    public async Task RebuildColdIndexesAsync(CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        foreach (var coldType in options.ColdEntityTypes)
        {
            var entityDir = GetEntityDirectory(coldType);
            if (!fs.DirectoryExists(entityDir))
                continue;

            tasks.Add(ColdEntityIndex.RebuildIndexAsync(
                fs, entityDir, coldType.Name, coldType,
                encryptionOptions.Key, ColdEntityStore.JsonOptions,
                logger, ct));
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Post-load FK validation: scans all loaded (hot) entities for dangling
    /// foreign key references and logs warnings. Does not modify data.
    /// </summary>
    public void ValidateForeignKeys()
    {
        var warnings = 0;

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.HasSharedClrType)
                continue;

            foreach (var fk in entityType.GetForeignKeys())
            {
                var principalType = fk.PrincipalEntityType.ClrType;

                // Skip validation for cold entity types (their FK targets aren't in memory)
                if (options.ColdEntityTypes.Contains(principalType))
                    continue;

                var principalSet = GetEntitySetAsQueryable(principalType);
                if (principalSet is null)
                    continue;

                var principalIds = new HashSet<Guid>(
                    principalSet.Cast<object>()
                        .Select(e => ((BaseEntity)e).Id));

                var dependentSet = GetEntitySetAsQueryable(entityType.ClrType);
                if (dependentSet is null)
                    continue;

                foreach (var fkProp in fk.Properties)
                {
                    var clrProp = fkProp.PropertyInfo;
                    if (clrProp is null)
                        continue;

                    foreach (var entity in dependentSet.Cast<object>())
                    {
                        var fkValue = clrProp.GetValue(entity);
                        if (fkValue is null || fkValue is Guid g && g == Guid.Empty)
                            continue;

                        if (fkValue is Guid fkGuid && !principalIds.Contains(fkGuid))
                        {
                            var entityId = ((BaseEntity)entity).Id;
                            logger.LogWarning(
                                "Dangling FK: {Type}.{Property} = {Value} on entity {Id} references missing {PrincipalType}",
                                entityType.ClrType.Name, fkProp.Name, fkGuid, entityId, principalType.Name);
                            warnings++;
                        }
                    }
                }
            }
        }

        if (warnings > 0)
            logger.LogWarning("FK validation found {Count} dangling reference(s)", warnings);
        else
            logger.LogDebug("FK validation passed — no dangling references");
    }

    private IQueryable<object>? GetEntitySetAsQueryable(Type clrType)
    {
        try
        {
            return (IQueryable<object>)context
                .GetType()
                .GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                .MakeGenericMethod(clrType)
                .Invoke(context, null)!;
        }
        catch
        {
            return null;
        }
    }
}

file static class DictionaryListExtensions
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
