using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Infrastructure.Persistence.JSON;

/// <summary>
/// Manages sharded on-disk index files (<c>_index_{Property}.json</c>) that map
/// foreign key values to entity file IDs for cold entity types. Each indexed
/// property gets its own shard file so that updates only rewrite the affected
/// shard, eliminating the serialization bottleneck on entity types with many
/// indexed properties (RGAP-8).
/// <para>
/// Each shard is a JSON dictionary:
/// <c>{ "&lt;guid&gt;": ["id1","id2"] }</c>
/// where the key is the foreign key value.
/// </para>
/// </summary>
internal sealed class ColdEntityIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Foreign key property names that are indexed for each cold entity type.
    /// Only properties that appear in query predicates are worth indexing.
    /// </summary>
    internal static readonly Dictionary<string, string[]> IndexedProperties = new()
    {
        ["ChatMessageDB"] = ["ChannelId", "ThreadId", "SenderAgentId"],
        ["AgentJobDB"] = ["ChannelId", "AgentId"],
        ["AgentJobLogEntryDB"] = ["AgentJobId"],
        ["TaskInstanceDB"] = ["TaskDefinitionId", "ChannelId"],
        ["TaskExecutionLogDB"] = ["TaskInstanceId"],
        ["TaskOutputEntryDB"] = ["TaskInstanceId"],
    };

    private static readonly Dictionary<string, HashSet<string>> KeyScopedIndexedProperties = new(StringComparer.Ordinal)
    {
        ["AgentJobLogEntryDB"] = new HashSet<string>(StringComparer.Ordinal) { "AgentJobId" }
    };

    /// <summary>
    /// Returns the shard file path for a given property:
    /// <c>{entityDir}/_index_{propertyName}.json</c>.
    /// </summary>
    internal static string GetShardPath(IPersistenceFileSystem fs, string entityDir, string propertyName)
        => fs.CombinePath(entityDir, $"_index_{propertyName}.json");

    internal static string GetKeyScopedShardPath(
        IPersistenceFileSystem fs,
        string entityDir,
        string propertyName,
        Guid key)
        => fs.CombinePath(
            GetKeyScopedShardDirectory(fs, entityDir, propertyName),
            $"{key:D}.json");

    /// <summary>
    /// Updates the sharded index files for a given entity directory after an entity
    /// is added, modified, or deleted. Only dirty shards are rewritten.
    /// </summary>
    /// <param name="indexedProperties">
    /// Optional merged index map from <c>ModuleColdIndexRegistry</c>. When
    /// <see langword="null"/> the static host <see cref="IndexedProperties"/> is used.
    /// </param>
    public static async Task UpdateIndexAsync(
        IPersistenceFileSystem fs,
        string entityDir,
        string entityTypeName,
        Guid entityId,
        object? entity,
        bool deleted,
        ILogger logger,
        CancellationToken ct,
        IReadOnlyDictionary<string, string[]>? indexedProperties = null)
    {
        var index = indexedProperties ?? IndexedProperties;
        if (!index.TryGetValue(entityTypeName, out var propNames))
            return;

        var type = entity?.GetType();
        var idStr = entityId.ToString();

        foreach (var propName in propNames)
        {
            if (IsKeyScoped(entityTypeName, propName))
            {
                await UpdateKeyScopedIndexAsync(
                    fs, entityDir, propName, idStr, entity, deleted, logger, ct);
                continue;
            }

            var shardPath = GetShardPath(fs, entityDir, propName);
            var shard = await LoadShardAsync(fs, shardPath, logger, ct);
            var dirty = false;

            // Remove old entries for this entity across all keys in this shard
            foreach (var ids in shard.Values)
            {
                if (ids.Remove(idStr))
                    dirty = true;
            }

            // Add new entry if not deleted
            if (!deleted && entity is not null && type is not null)
            {
                var prop = type.GetProperty(propName);
                var value = prop?.GetValue(entity);
                if (value is not null)
                {
                    var key = value.ToString()!;
                    if (!shard.TryGetValue(key, out var ids))
                    {
                        ids = [];
                        shard[key] = ids;
                    }
                    if (!ids.Contains(idStr))
                    {
                        ids.Add(idStr);
                        dirty = true;
                    }
                }
            }

            if (!dirty)
                continue;

            // Prune empty keys
            var emptyKeys = shard.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
            foreach (var k in emptyKeys)
                shard.Remove(k);

            await SaveShardAsync(fs, shardPath, shard, ct);
        }
    }

    /// <summary>
    /// Looks up entity IDs from the sharded index for a given foreign key filter.
    /// Returns <c>null</c> if no shard exists or the key is not found,
    /// signaling the caller should fall back to a full scan.
    /// </summary>
    public static async Task<HashSet<Guid>?> LookupAsync(
        IPersistenceFileSystem fs,
        string entityDir,
        string propertyName,
        Guid value,
        ILogger logger,
        CancellationToken ct,
        string? entityTypeName = null)
    {
        entityTypeName ??= GetEntityTypeName(entityDir);
        if (IsKeyScoped(entityTypeName, propertyName))
        {
            var scopedPath = GetKeyScopedShardPath(fs, entityDir, propertyName, value);
            if (fs.FileExists(scopedPath))
                return await LoadKeyScopedIdsAsync(fs, scopedPath, logger, ct);

            var scopedDir = GetKeyScopedShardDirectory(fs, entityDir, propertyName);
            if (fs.DirectoryExists(scopedDir))
                return [];

            var legacy = await LookupLegacyShardAsync(fs, entityDir, propertyName, value, logger, ct);
            if (legacy is not null)
                return legacy;

            return null;
        }

        return await LookupLegacyShardAsync(fs, entityDir, propertyName, value, logger, ct);
    }

    private static async Task<HashSet<Guid>?> LookupLegacyShardAsync(
        IPersistenceFileSystem fs,
        string entityDir,
        string propertyName,
        Guid value,
        ILogger logger,
        CancellationToken ct)
    {
        var shardPath = GetShardPath(fs, entityDir, propertyName);
        if (!fs.FileExists(shardPath))
            return null;

        var shard = await LoadShardAsync(fs, shardPath, logger, ct);
        var key = value.ToString();

        if (!shard.TryGetValue(key, out var ids))
            return null;

        var result = new HashSet<Guid>();
        foreach (var idStr in ids)
        {
            if (Guid.TryParse(idStr, out var id))
                result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Rebuilds the sharded indexes from scratch by scanning all entity files in the directory.
    /// Used during startup or when indexes are corrupted.
    /// </summary>
    /// <param name="indexedProperties">
    /// Optional merged index map from <c>ModuleColdIndexRegistry</c>. When
    /// <see langword="null"/> the static host <see cref="IndexedProperties"/> is used.
    /// </param>
    public static async Task RebuildIndexAsync(
        IPersistenceFileSystem fs,
        string entityDir,
        string entityTypeName,
        Type entityClrType,
        byte[] encryptionKey,
        JsonSerializerOptions deserializeOptions,
        ILogger logger,
        CancellationToken ct,
        IReadOnlyDictionary<string, string[]>? indexedProperties = null)
    {
        var index = indexedProperties ?? IndexedProperties;
        if (!index.TryGetValue(entityTypeName, out var propNames))
            return;

        var files = fs.GetFiles(entityDir, "*.json")
            .Where(f => !fs.GetFileName(f).StartsWith('_'))
            .ToArray();

        if (files.Length == 0)
            return;

        // Build per-property shards in memory
        var shards = new Dictionary<string, Dictionary<string, List<string>>>();
        var keyScopedShards = new Dictionary<string, Dictionary<string, List<string>>>();
        foreach (var propName in propNames)
        {
            if (IsKeyScoped(entityTypeName, propName))
                keyScopedShards[propName] = new Dictionary<string, List<string>>();
            else
                shards[propName] = new Dictionary<string, List<string>>();
        }

        foreach (var file in files)
        {
            try
            {
                var json = await JsonFileEncryption.ReadJsonAsync(fs, file, encryptionKey, ct);
                var entity = JsonSerializer.Deserialize(json, entityClrType, deserializeOptions);
                if (entity is null) continue;

                var idProp = entityClrType.GetProperty("Id");
                if (idProp?.GetValue(entity) is not Guid entityId)
                    continue;

                foreach (var propName in propNames)
                {
                    var prop = entityClrType.GetProperty(propName);
                    if (prop is null) continue;

                    var value = prop.GetValue(entity);
                    if (value is null) continue;

                    var key = value.ToString()!;
                    var shard = IsKeyScoped(entityTypeName, propName)
                        ? keyScopedShards[propName]
                        : shards[propName];
                    if (!shard.TryGetValue(key, out var ids))
                    {
                        ids = [];
                        shard[key] = ids;
                    }
                    ids.Add(entityId.ToString());
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to index cold entity from {Path}", file);
            }
        }

        // Write each shard
        var totalKeys = 0;
        foreach (var (propName, shard) in shards)
        {
            totalKeys += shard.Count;
            var shardPath = GetShardPath(fs, entityDir, propName);
            await SaveShardAsync(fs, shardPath, shard, ct);
        }

        foreach (var (propName, shard) in keyScopedShards)
        {
            totalKeys += shard.Count;
            await ReplaceKeyScopedShardsAsync(fs, entityDir, propName, shard, ct);

            var legacyShardPath = GetShardPath(fs, entityDir, propName);
            if (fs.FileExists(legacyShardPath))
                fs.DeleteFile(legacyShardPath);
        }

        // Clean up legacy monolithic _index.json if present
        var legacyPath = fs.CombinePath(entityDir, "_index.json");
        if (fs.FileExists(legacyPath))
        {
            fs.DeleteFile(legacyPath);
            logger.LogInformation("Removed legacy _index.json for {Type}", entityTypeName);
        }

        logger.LogInformation("Rebuilt cold index for {Type}: {Shards} shards, {Keys} keys, {Files} files",
            entityTypeName, propNames.Length, totalKeys, files.Length);
    }

    /// <summary>
    /// Cleans up leftover <c>.tmp</c> files in the given directory.
    /// These are artifacts of interrupted <see cref="AtomicFileWriter"/> writes.
    /// </summary>
    public static void CleanupTempFiles(IPersistenceFileSystem fs, string directory, ILogger logger)
    {
        var tmpFiles = fs.GetFiles(directory, "*.tmp");
        foreach (var tmp in tmpFiles)
        {
            try
            {
                fs.DeleteFile(tmp);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete orphan temp file {Path}", tmp);
            }
        }
        if (tmpFiles.Length > 0)
            logger.LogInformation("Cleaned up {Count} orphan .tmp files in {Dir}", tmpFiles.Length, directory);
    }

    private static bool IsKeyScoped(string? entityTypeName, string propertyName)
        => entityTypeName is not null
           && KeyScopedIndexedProperties.TryGetValue(entityTypeName, out var properties)
           && properties.Contains(propertyName);

    private static string GetEntityTypeName(string entityDir)
        => Path.GetFileName(entityDir.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));

    private static string GetKeyScopedShardDirectory(
        IPersistenceFileSystem fs,
        string entityDir,
        string propertyName)
        => fs.CombinePath(entityDir, $"_index_{propertyName}");

    private static async Task UpdateKeyScopedIndexAsync(
        IPersistenceFileSystem fs,
        string entityDir,
        string propertyName,
        string entityId,
        object? entity,
        bool deleted,
        ILogger logger,
        CancellationToken ct)
    {
        if (deleted || entity is null)
        {
            await RemoveFromKeyScopedShardsAsync(
                fs, entityDir, propertyName, entityId, logger, ct);
            return;
        }

        var prop = entity.GetType().GetProperty(propertyName);
        if (prop?.GetValue(entity) is not Guid key)
        {
            await RemoveFromKeyScopedShardsAsync(
                fs, entityDir, propertyName, entityId, logger, ct);
            return;
        }

        var shardPath = GetKeyScopedShardPath(fs, entityDir, propertyName, key);
        var ids = await LoadKeyScopedIdStringsAsync(fs, shardPath, logger, ct);
        if (ids.Contains(entityId))
            return;

        ids.Add(entityId);
        await SaveKeyScopedShardAsync(fs, shardPath, ids, ct);
    }

    private static async Task RemoveFromKeyScopedShardsAsync(
        IPersistenceFileSystem fs,
        string entityDir,
        string propertyName,
        string entityId,
        ILogger logger,
        CancellationToken ct)
    {
        var shardDir = GetKeyScopedShardDirectory(fs, entityDir, propertyName);
        if (!fs.DirectoryExists(shardDir))
            return;

        foreach (var file in fs.GetFiles(shardDir, "*.json"))
        {
            var ids = await LoadKeyScopedIdStringsAsync(fs, file, logger, ct);
            if (!ids.Remove(entityId))
                continue;

            if (ids.Count == 0)
                fs.DeleteFile(file);
            else
                await SaveKeyScopedShardAsync(fs, file, ids, ct);
        }
    }

    private static async Task ReplaceKeyScopedShardsAsync(
        IPersistenceFileSystem fs,
        string entityDir,
        string propertyName,
        Dictionary<string, List<string>> shard,
        CancellationToken ct)
    {
        var shardDir = GetKeyScopedShardDirectory(fs, entityDir, propertyName);
        if (fs.DirectoryExists(shardDir))
        {
            foreach (var file in fs.GetFiles(shardDir, "*.json"))
                fs.DeleteFile(file);
        }

        fs.CreateDirectory(shardDir);
        foreach (var (key, ids) in shard)
        {
            if (!Guid.TryParse(key, out var guidKey))
                continue;

            await SaveKeyScopedShardAsync(
                fs,
                GetKeyScopedShardPath(fs, entityDir, propertyName, guidKey),
                ids,
                ct);
        }
    }

    private static async Task<HashSet<Guid>?> LoadKeyScopedIdsAsync(
        IPersistenceFileSystem fs,
        string shardPath,
        ILogger logger,
        CancellationToken ct)
    {
        var idStrings = await LoadKeyScopedIdStringsAsync(fs, shardPath, logger, ct);
        if (idStrings.Count == 0)
            return null;

        var result = new HashSet<Guid>();
        foreach (var idStr in idStrings)
        {
            if (Guid.TryParse(idStr, out var id))
                result.Add(id);
        }

        return result.Count == 0 ? null : result;
    }

    private static async Task<List<string>> LoadKeyScopedIdStringsAsync(
        IPersistenceFileSystem fs,
        string shardPath,
        ILogger logger,
        CancellationToken ct)
    {
        if (!fs.FileExists(shardPath))
            return [];

        try
        {
            var json = await fs.ReadAllTextAsync(shardPath, ct);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read key-scoped index shard {Path}, rebuilding", shardPath);
            return [];
        }
    }

    private static async Task SaveKeyScopedShardAsync(
        IPersistenceFileSystem fs,
        string shardPath,
        List<string> ids,
        CancellationToken ct,
        bool fsync = true)
    {
        var shardDir = Path.GetDirectoryName(shardPath);
        if (!string.IsNullOrEmpty(shardDir))
            fs.CreateDirectory(shardDir);

        var json = JsonSerializer.Serialize(ids, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(fs, shardPath, json, fsync, ct);
    }

    private static async Task<Dictionary<string, List<string>>> LoadShardAsync(
        IPersistenceFileSystem fs, string shardPath, ILogger logger, CancellationToken ct)
    {
        if (!fs.FileExists(shardPath))
            return new Dictionary<string, List<string>>();

        try
        {
            var json = await fs.ReadAllTextAsync(shardPath, ct);
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read index shard {Path}, rebuilding", shardPath);
            return new Dictionary<string, List<string>>();
        }
    }

    private static async Task SaveShardAsync(
        IPersistenceFileSystem fs, string shardPath, Dictionary<string, List<string>> shard,
        CancellationToken ct, bool fsync = true)
    {
        var json = JsonSerializer.Serialize(shard, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(fs, shardPath, json, fsync, ct);
    }
}
