using System.Text.Json;

namespace SharpClaw.Infrastructure.Persistence.JSON;

/// <summary>
/// Upgrades a single entity's JSON payload from one schema version to the next.
/// <para>
/// Implementations are registered with <see cref="EntityMigrationRegistry"/> and
/// chained by <c>EntityMigratorPipeline&lt;T&gt;</c> (not yet implemented — RGAP-9).
/// Each migrator handles exactly one version step: <see cref="FromVersion"/> → <see cref="ToVersion"/>.
/// </para>
/// </summary>
/// <typeparam name="T">The entity type this migrator handles.</typeparam>
internal interface IEntityMigrator<T>
{
    /// <summary>The schema version this migrator reads.</summary>
    int FromVersion { get; }

    /// <summary>The schema version this migrator produces.</summary>
    int ToVersion { get; }

    /// <summary>
    /// Transforms the raw JSON element of a <typeparamref name="T"/> entity from
    /// <see cref="FromVersion"/> to <see cref="ToVersion"/> and returns the upgraded
    /// JSON element.
    /// </summary>
    JsonElement Migrate(JsonElement source);
}
