namespace SharpClaw.Infrastructure.Persistence.JSON;

/// <summary>
/// Holds <see cref="IEntityMigrator{T}"/> registrations for all entity types.
/// <para>
/// Currently a no-op placeholder (RGAP-9 groundwork). Register this as a singleton in DI.
/// When the migration pipeline is implemented, callers will use
/// <c>GetMigrators&lt;T&gt;()</c> to retrieve the ordered chain for a given entity type
/// and schema version.
/// </para>
/// </summary>
internal sealed class EntityMigrationRegistry
{
    private readonly Dictionary<Type, List<object>> _migrators = [];

    /// <summary>
    /// Registers a migrator for entity type <typeparamref name="T"/>.
    /// Migrators are stored in registration order; the pipeline will sort by
    /// <see cref="IEntityMigrator{T}.FromVersion"/> before applying.
    /// </summary>
    internal void Register<T>(IEntityMigrator<T> migrator)
    {
        if (!_migrators.TryGetValue(typeof(T), out var list))
        {
            list = [];
            _migrators[typeof(T)] = list;
        }
        list.Add(migrator);
    }

    /// <summary>
    /// Returns the registered migrators for <typeparamref name="T"/> that form
    /// a chain from <paramref name="fromVersion"/> up to <see cref="JsonSchemaVersion.Current"/>,
    /// ordered by <see cref="IEntityMigrator{T}.FromVersion"/> ascending.
    /// Returns an empty list when no migrations are needed or registered.
    /// </summary>
    internal IReadOnlyList<IEntityMigrator<T>> GetMigrators<T>(int fromVersion)
    {
        if (fromVersion >= JsonSchemaVersion.Current)
            return [];

        if (!_migrators.TryGetValue(typeof(T), out var list))
            return [];

        return list
            .Cast<IEntityMigrator<T>>()
            .Where(m => m.FromVersion >= fromVersion)
            .OrderBy(m => m.FromVersion)
            .ToList();
    }
}
