using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
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
    /// Loads all per-entity JSON files from the data directory into the InMemory database.
    /// Each entity type has its own sub-folder under the data directory, and each entity
    /// is stored as an individual <c>{Id}.json</c> file.
    /// Call once at startup after the DbContext is configured.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.DataDirectory);

        // Collect navigation property names per CLR type so we can null them
        // after deserialization, preventing EF from traversing the object graph
        // and hitting duplicate-key conflicts on related entities.
        var navigations = context.Model.GetEntityTypes()
            .GroupBy(e => e.ClrType)
            .ToDictionary(
                g => g.Key,
                g => g.First()
                    .GetNavigations()
                    .Select(n => n.PropertyInfo)
                    .Where(p => p is not null)
                    .ToList());

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            // Skip shared-type entities (e.g. many-to-many join tables)
            // — they don't have their own DbSet or JSON file.
            if (entityType.HasSharedClrType)
                continue;

            var clrType = entityType.ClrType;
            var entityDir = GetEntityDirectory(clrType);

            if (!Directory.Exists(entityDir))
                continue;

            var navProps = navigations.GetValueOrDefault(clrType);
            var files = Directory.GetFiles(entityDir, "*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct);
                    var entity = JsonSerializer.Deserialize(json, clrType, JsonOptions);

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
                    logger.LogError(ex, "Failed to load {Type} from {Path}", clrType.Name, file);
                }
            }

            logger.LogDebug("Loaded {Count} {Type} from {Path}", files.Length, clrType.Name, entityDir);
        }

        await context.SaveChangesAsync(ct);
        DetachAll();
    }

    /// <summary>
    /// Saves all tracked entity types from the InMemory database to individual JSON files.
    /// Each entity is written as <c>{DataDirectory}/{EntityType}/{Id}.json</c>.
    /// Orphan files for deleted entities are removed.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.DataDirectory);

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            // Skip shared-type entities (e.g. many-to-many join tables)
            if (entityType.HasSharedClrType)
                continue;

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

                foreach (var entity in entities)
                {
                    var id = ((BaseEntity)entity).Id;
                    activeIds.Add($"{id}.json");

                    var filePath = Path.Combine(entityDir, $"{id}.json");
                    var json = JsonSerializer.Serialize(entity, clrType, JsonOptions);
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

    private string GetEntityDirectory(Type entityType)
        => Path.Combine(options.DataDirectory, entityType.Name);

    private void DetachAll()
    {
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            entry.State = EntityState.Detached;
        }
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
