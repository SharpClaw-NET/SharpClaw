using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Infrastructure.Persistence.JSON;

/// <summary>
/// Disk-backed read path for cold entity types that are not loaded into the
/// InMemory EF store at startup. Reads individual JSON files on demand,
/// transparently handling AES-256-GCM encryption via <see cref="JsonFileEncryption"/>.
/// <para>
/// Writes still flow through EF + <see cref="JsonFilePersistenceService"/>;
/// this class is read-only.
/// </para>
/// <para>
/// <b>Phase F (RGAP-14):</b> Read operations use exponential backoff retries
/// (3 attempts: 100 ms → 500 ms → 2 s) for transient I/O errors. Files that
/// remain unreadable after all retries are quarantined to
/// <c>_quarantine/{entityType}/</c>.
/// </para>
/// <para>
/// <b>Phase F (RGAP-15):</b> Public read methods return <see cref="ReadResult{T}"/>
/// so callers can distinguish "entity doesn't exist" from "entity exists but is
/// unreadable" and surface meaningful diagnostics instead of silent nulls.
/// </para>
/// <para>
/// When an on-disk <c>_index_{property}.json</c> exists for the entity type
/// (maintained by <see cref="ColdEntityIndex"/>), callers can pass an
/// <see cref="IndexFilter"/> to narrow reads to only the files that match a
/// foreign key, avoiding O(N) full-directory scans.
/// </para>
/// </summary>
public sealed class ColdEntityStore(
    IPersistenceFileSystem fs,
    JsonFileOptions options,
    EncryptionOptions encryptionOptions,
    ILogger<ColdEntityStore> logger,
    DirectoryLockManager? directoryLockManager = null,
    ISnapshotFallback? snapshotFallback = null,
    FlushQueue? flushQueue = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter(), new NullableGuidConverter() }
    };

    /// <summary>
    /// Hint for <see cref="QueryAsync{T}"/> / <see cref="QueryAllAsync{T}"/>
    /// to use the on-disk index instead of scanning every file.
    /// </summary>
    public readonly record struct IndexFilter(string PropertyName, Guid Value);

    /// <summary>
    /// Loads a single cold entity by its primary key.
    /// Returns <see cref="ReadResult{T}.NotFound"/> when the file does not exist,
    /// <see cref="ReadResult{T}.Corrupted"/> when the file is unreadable (quarantined),
    /// or <see cref="ReadResult{T}.IoError"/> on persistent I/O failure.
    /// </summary>
    public async Task<ReadResult<T>> FindAsync<T>(Guid id, CancellationToken ct = default)
        where T : BaseEntity
    {
        // Phase K (RGAP-4): Check write-through overlay before disk.
        if (flushQueue is not null && flushQueue.Overlay.TryGetValue((typeof(T).Name, id), out var overlayBytes))
        {
            if (overlayBytes is null)
                return new ReadResult<T>.NotFound(); // Tombstone — entity was deleted.

            try
            {
                var entity = JsonSerializer.Deserialize<T>(overlayBytes, JsonOptions);
                return entity is not null
                    ? new ReadResult<T>.Success(entity)
                    : new ReadResult<T>.NotFound();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize {Type} {Id} from overlay, falling through to disk", typeof(T).Name, id);
            }
        }

        var dir = fs.CombinePath(options.DataDirectory, typeof(T).Name);
        var path = fs.CombinePath(dir, $"{id}.json");
        if (!fs.FileExists(path))
            return new ReadResult<T>.NotFound();

        using var _ = directoryLockManager is not null
            ? await directoryLockManager.AcquireAsync(dir, ct)
            : null;

        var readResult = await QuarantineService.ReadBytesWithRetryAsync(
            fs, path, dir, encryptionOptions.Key, logger, ct,
            snapshotFallback, options.DataDirectory);

        if (!readResult.IsSuccess)
        {
            return readResult.Outcome == QuarantineService.ReadBytesOutcome.IoError
                ? new ReadResult<T>.IoError(readResult.Exception!, readResult.FilePath!)
                : new ReadResult<T>.Corrupted(readResult.Exception!, readResult.FilePath!);
        }

        // Phase J: Optional read-time checksum verification.
        if (options.EnableChecksums && options.VerifyChecksumsOnRead)
        {
            var fileName = fs.GetFileName(path);
            var valid = await ChecksumManifest.VerifyFileAsync(
                fs, dir, fileName, readResult.Data!.Memory, encryptionOptions.Key, logger, ct,
                options.FsyncOnWrite);
            if (!valid)
            {
                readResult.Dispose();
                QuarantineService.MoveToQuarantine(fs, path, dir, logger);
                return new ReadResult<T>.Corrupted(
                    new InvalidOperationException("Checksum mismatch — silent corruption detected"), path);
            }
        }

        try
        {
            using (readResult)
            {
                var entity = JsonSerializer.Deserialize<T>(readResult.Data!.Span, JsonOptions);
                return entity is not null
                    ? new ReadResult<T>.Success(entity)
                    : new ReadResult<T>.Corrupted(
                        new InvalidOperationException("Deserialization returned null"), path);
            }
        }
        catch (Exception ex)
        {
            QuarantineService.MoveToQuarantine(fs, path, dir, logger);
            return new ReadResult<T>.Corrupted(ex, path);
        }
    }

    /// <summary>
    /// Queries cold entities matching <paramref name="predicate"/>, ordered by
    /// <see cref="BaseEntity.CreatedAt"/> descending, limited to
    /// <paramref name="limit"/> results, then re-sorted chronologically.
    /// </summary>
    /// <param name="predicate">Filter applied after deserialization.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="indexFilter">
    /// Optional index hint. When provided and an index shard exists,
    /// only the files matching the indexed foreign key are read.
    /// </param>
    public async Task<List<T>> QueryAsync<T>(
        Func<T, bool> predicate,
        int limit,
        CancellationToken ct = default,
        IndexFilter? indexFilter = null) where T : BaseEntity
    {
        var entities = await ReadEntitiesAsync<T>(indexFilter, ct);
        return entities
            .Where(predicate)
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .OrderBy(e => e.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Returns all matching entities without a limit, ordered chronologically.
    /// </summary>
    public async Task<List<T>> QueryAllAsync<T>(
        Func<T, bool> predicate,
        CancellationToken ct = default,
        IndexFilter? indexFilter = null) where T : BaseEntity
    {
        var entities = await ReadEntitiesAsync<T>(indexFilter, ct);
        return entities
            .Where(predicate)
            .OrderBy(e => e.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Core read logic shared by both query methods. Reads files with retry and
    /// quarantine; failed files are logged and skipped (never surface as nulls).
    /// When an index filter is provided, uses <see cref="ColdEntityIndex.LookupAsync"/>
    /// to read only matching files; otherwise falls back to a full directory scan.
    /// </summary>
    private async Task<List<T>> ReadEntitiesAsync<T>(
        IndexFilter? indexFilter, CancellationToken ct) where T : BaseEntity
    {
        var dir = fs.CombinePath(options.DataDirectory, typeof(T).Name);
        if (!fs.DirectoryExists(dir))
            return [];

        using var _ = directoryLockManager is not null
            ? await directoryLockManager.AcquireAsync(dir, ct)
            : null;

        string[] files;

        if (indexFilter is { } filter)
        {
            var indexedIds = await ColdEntityIndex.LookupAsync(
                fs, dir, filter.PropertyName, filter.Value, logger, ct, typeof(T).Name);

            if (indexedIds is not null)
            {
                files = indexedIds
                    .Select(id => fs.CombinePath(dir, $"{id}.json"))
                    .Where(fs.FileExists)
                    .ToArray();
            }
            else
            {
                files = GetEntityFiles(dir);
            }
        }
        else
        {
            files = GetEntityFiles(dir);
        }

        if (files.Length == 0)
            return [];

        var results = new List<T>(files.Length);
        var verifyChecksums = options.EnableChecksums && options.VerifyChecksumsOnRead;
        var readTasks = files.Select(async file =>
        {
            var readResult = await QuarantineService.ReadBytesWithRetryAsync(
                fs, file, dir, encryptionOptions.Key, logger, ct,
                snapshotFallback, options.DataDirectory);

            if (!readResult.IsSuccess)
                return null;

            // Phase J: Optional read-time checksum verification.
            if (verifyChecksums)
            {
                var fName = fs.GetFileName(file);
                var valid = await ChecksumManifest.VerifyFileAsync(
                    fs, dir, fName, readResult.Data!.Memory, encryptionOptions.Key, logger, ct,
                    options.FsyncOnWrite);
                if (!valid)
                {
                    readResult.Dispose();
                    QuarantineService.MoveToQuarantine(fs, file, dir, logger);
                    return null;
                }
            }

            try
            {
                using (readResult)
                    return JsonSerializer.Deserialize<T>(readResult.Data!.Span, JsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize cold entity from {Path}; quarantining", file);
                QuarantineService.MoveToQuarantine(fs, file, dir, logger);
                return null;
            }
        });

        foreach (var entity in await Task.WhenAll(readTasks))
        {
            if (entity is not null)
                results.Add(entity);
        }

        return results;
    }

    private string[] GetEntityFiles(string dir)
    {
        return fs.GetFiles(dir, "*.json")
            .Where(f =>
            {
                var name = fs.GetFileName(f);
                return !name.StartsWith('_');
            })
            .ToArray();
    }

    /// <summary>
    /// Handles JSON <c>null</c> for non-nullable <see cref="Guid"/> properties
    /// by substituting <see cref="Guid.Empty"/>.
    /// </summary>
    private sealed class NullableGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? Guid.Empty : reader.GetGuid();

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
