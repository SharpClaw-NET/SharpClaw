using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Infrastructure.Persistence.JSON;

/// <summary>
/// Handles loading entities from and saving entities to JSON files on disk,
/// keeping the InMemory EFC database in sync with the file system.
/// </summary>
public sealed class JsonFilePersistenceService(
    SharpClawDbContext context,
    JsonFileOptions options,
    ILogger<JsonFilePersistenceService> logger)
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
                    .ToList()!);
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
        Directory.CreateDirectory(options.DataDirectory);

        var navigations = GetNavigations();

        // First pass: load all regular (non-join) entities so FK targets exist.
        // File I/O is parallelised per entity type to reduce wall-clock time.
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.HasSharedClrType)
                continue;

            var clrType = entityType.ClrType;
            var entityDir = GetEntityDirectory(clrType);

            if (!Directory.Exists(entityDir))
                continue;

            var navProps = navigations.GetValueOrDefault(clrType);
            var files = Directory.GetFiles(entityDir, "*.json");

            if (files.Length == 0)
                continue;

            // Read all files for this entity type in parallel.
            var readTasks = files.Select(async file =>
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct);
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
        var sentinelPath = Path.Combine(options.DataDirectory, ".compacted");
        if (File.Exists(sentinelPath))
            return;

        logger.LogInformation("Recompacting JSON data files (one-time migration)...");
        await FlushAsync(ct);

        await File.WriteAllTextAsync(sentinelPath, DateTimeOffset.UtcNow.ToString("O"), ct);
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
        Directory.CreateDirectory(options.DataDirectory);

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.HasSharedClrType)
            {
                await FlushJoinTableAsync(entityType, ct);
                continue;
            }

            var clrType = entityType.ClrType;
            var entityDir = GetEntityDirectory(clrType);
            Directory.CreateDirectory(entityDir);

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

                    var filePath = Path.Combine(entityDir, $"{id}.json");
                    var json = SerializeEntity(entity, clrType, navProps);
                    await File.WriteAllTextAsync(filePath, json, ct);
                }

                // Remove orphan files for entities that no longer exist
                foreach (var file in Directory.GetFiles(entityDir, "*.json"))
                {
                    if (!activeIds.Contains(Path.GetFileName(file)))
                        File.Delete(file);
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
        Directory.CreateDirectory(options.DataDirectory);

        foreach (var (clrType, id, state) in entityChanges)
        {
            var entityDir = GetEntityDirectory(clrType);
            Directory.CreateDirectory(entityDir);
            var filePath = Path.Combine(entityDir, $"{id}.json");

            try
            {
                if (state == EntityState.Deleted)
                {
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                }
                else
                {
                    // After base.SaveChangesAsync the entity is tracked as
                    // Unchanged.  Find returns the local (tracked) instance
                    // first, falling back to the InMemory store.
                    var entity = context.Find(clrType, id);
                    if (entity is not null)
                    {
                        var navProps = GetNavigations().GetValueOrDefault(clrType);
                        var json = SerializeEntity(entity, clrType, navProps);
                        await File.WriteAllTextAsync(filePath, json, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to flush {Type} {Id} to {Path}",
                    clrType.Name, id, filePath);
            }
        }

        if (joinTableChanges.Count > 0)
        {
            foreach (var entityType in context.Model.GetEntityTypes())
            {
                if (!entityType.HasSharedClrType)
                    continue;
                if (!joinTableChanges.Contains(entityType.Name))
                    continue;

                await FlushJoinTableAsync(entityType, ct);
            }
        }
    }

    private string GetEntityDirectory(Type entityType)
        => Path.Combine(options.DataDirectory, entityType.Name);

    private string GetJoinTableDirectory(string tableName)
        => Path.Combine(options.DataDirectory, tableName);

    /// <summary>
    /// Flushes a shared-type (many-to-many join table) entity to a single
    /// <c>_rows.json</c> file. Each row is serialised as a dictionary of
    /// FK property names to their <see cref="Guid"/> values.
    /// </summary>
    private async Task FlushJoinTableAsync(IEntityType entityType, CancellationToken ct)
    {
        var tableName = entityType.Name;
        var tableDir = GetJoinTableDirectory(tableName);
        Directory.CreateDirectory(tableDir);

        try
        {
            var fkProperties = entityType.GetProperties()
                .Where(p => p.IsForeignKey())
                .ToList();

            var dbSet = context.Set<Dictionary<string, object>>(tableName);
            var rows = await dbSet.ToListAsync(ct);

            var filePath = Path.Combine(tableDir, "_rows.json");

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
                await File.WriteAllTextAsync(filePath, json, ct);
            }
            else if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            logger.LogDebug("Flushed {Count} {Table} join rows", rows.Count, tableName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to flush join table {Table}", tableName);
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
        var filePath = Path.Combine(tableDir, "_rows.json");

        if (!File.Exists(filePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
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
}
