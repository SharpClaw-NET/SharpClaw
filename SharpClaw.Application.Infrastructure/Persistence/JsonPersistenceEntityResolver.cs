using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities;
using SharpClaw.Infrastructure.Persistence.JSON;

namespace SharpClaw.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPersistenceEntityResolver"/> implementation for JSON/InMemory
/// mode. Queries EF first; when an entity is absent and the entity type is
/// configured as cold, falls back to <see cref="ColdEntityStore"/>, then
/// attaches the hydrated entity to the supplied context so subsequent EF
/// operations work correctly.
/// </summary>
public sealed class JsonPersistenceEntityResolver(
    JsonFileOptions options,
    ColdEntityStore coldStore,
    ILogger<JsonPersistenceEntityResolver>? logger = null) : IPersistenceEntityResolver
{
    /// <summary>
    /// Attach a cold-hydrated entity defensively. A corrupt cold record can
    /// carry a navigation graph with null FK values that throws out of EF's
    /// <c>NavigationFixer</c> the moment <see cref="DbContext.Attach{T}"/>
    /// walks the graph. Such failures must not propagate out of cold-store
    /// paths — they would otherwise tear down callers like
    /// <c>TaskRuntimeHost.RecoverStaleInstancesAsync</c> and (because that
    /// caller is a <c>BackgroundService</c> with <c>StopHost</c> behaviour)
    /// take the entire host down on startup.
    /// <para>
    /// Cold-only entities are introduced to the hot context as
    /// <see cref="EntityState.Added"/> rather than <see cref="EntityState.Unchanged"/>.
    /// The InMemory provider has no row for a cold-only PK, so a later
    /// mutate + <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// against an <see cref="EntityState.Modified"/> entry throws
    /// <c>DbUpdateConcurrencyException</c> ("entity does not exist in the
    /// store"). Treating cold reads as Added lazily promotes them to the hot
    /// store on first save; the JSON flush is keyed by entity Id and is
    /// idempotent across Added/Modified, so existing cold files round-trip
    /// cleanly.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>true</c> when the entity is now tracked by the context, <c>false</c>
    /// when attach was skipped due to a corrupt graph. The entity reference
    /// itself is still returned to the caller in the failure case so cold
    /// reads remain observable; it just is not change-tracked.
    /// </returns>
    private bool TryAttachCold<T>(SharpClawDbContext db, T entity) where T : BaseEntity
    {
        var rootEntry = db.Entry(entity);
        if (rootEntry.State != EntityState.Detached)
            return true;

        try
        {
            // Use root-only state assignment instead of db.Attach / db.Set.Add.
            // Both of those walk the navigation graph and try to track every
            // referenced entity, which causes two distinct failures with cold
            // records:
            //   * Attach throws ArgumentNullException inside NavigationFixer
            //     when a navigation has a null FK key.
            //   * Add cascades into Add for already-tracked principals (e.g.
            //     TaskDefinitionDB), and InMemoryTable.Create then throws
            //     ArgumentException ("same key") on the principal's PK.
            // Setting state directly on the root entry tracks just this one
            // entity; navigation properties stay detached. The cold record is
            // observable, its own SaveChanges round-trips through the JSON
            // flush (which is keyed by Id and idempotent across Added/Modified),
            // and EF will not try to insert/update referenced graph members.
            rootEntry.State = EntityState.Added;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentNullException or ArgumentException or InvalidOperationException)
        {
            logger?.LogWarning(
                ex,
                "Skipping EF attach for cold-hydrated {EntityType} {EntityId}: {Reason}. Entity is returned untracked.",
                typeof(T).Name,
                entity.Id,
                ex.GetType().Name);

            try
            {
                if (rootEntry.State != EntityState.Detached)
                    rootEntry.State = EntityState.Detached;
            }
            catch
            {
                // Best-effort detach; swallow any secondary failure.
            }

            return false;
        }
    }

    public async Task<T?> FindAsync<T>(SharpClawDbContext db, Guid id, CancellationToken ct = default)
        where T : BaseEntity
    {
        var tracked = await db.Set<T>().FindAsync([id], ct);
        if (tracked is not null)
            return tracked;

        if (!options.ColdEntityTypes.Contains(typeof(T)))
            return null;

        var result = await coldStore.FindAsync<T>(id, ct);
        var cold = result.ValueOrDefault;
        if (cold is null)
            return null;

        // Attach only when not already tracked (concurrent FindAsync paths).
        TryAttachCold(db, cold);

        return cold;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        SharpClawDbContext db,
        Expression<Func<T, bool>> predicate,
        PersistenceQueryHint? hint = null,
        CancellationToken ct = default)
        where T : BaseEntity
    {
        var hot = await db.Set<T>().Where(predicate).OrderBy(e => e.CreatedAt).ToListAsync(ct);

        if (!options.ColdEntityTypes.Contains(typeof(T)))
            return hot;

        var indexFilter = hint is not null
            ? new ColdEntityStore.IndexFilter(hint.PropertyName, hint.Value)
            : (ColdEntityStore.IndexFilter?)null;

        var cold = await coldStore.QueryAllAsync<T>(predicate.Compile(), ct, indexFilter);

        var hotIds = hot.Select(e => e.Id).ToHashSet();
        foreach (var entity in cold)
        {
            if (hotIds.Contains(entity.Id))
                continue;

            // The cold record's predicate match may be stale: the hot store
            // may already contain a row with this PK whose current state no
            // longer matches the predicate. In that case hot wins and we
            // discard the cold copy — adding it would collide with the
            // existing InMemory row.
            if (db.Set<T>().Find(entity.Id) is not null)
                continue;

            TryAttachCold(db, entity);

            hot.Add(entity);
        }

        return hot.OrderBy(e => e.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        SharpClawDbContext db,
        Expression<Func<T, bool>> predicate,
        int limit,
        PersistenceQueryHint? hint = null,
        CancellationToken ct = default)
        where T : BaseEntity
    {
        // We need more results than the limit to account for de-duplication
        // between hot and cold sets, so we query cold unbounded then merge.
        var hot = await db.Set<T>().Where(predicate).OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync(ct);

        if (!options.ColdEntityTypes.Contains(typeof(T)))
            return hot.OrderBy(e => e.CreatedAt).ToList();

        var indexFilter = hint is not null
            ? new ColdEntityStore.IndexFilter(hint.PropertyName, hint.Value)
            : (ColdEntityStore.IndexFilter?)null;

        // QueryAsync on ColdEntityStore already orders descending then re-orders asc within limit.
        var cold = await coldStore.QueryAsync<T>(predicate.Compile(), limit, ct, indexFilter);

        var hotIds = hot.Select(e => e.Id).ToHashSet();
        foreach (var entity in cold)
        {
            if (hotIds.Contains(entity.Id))
                continue;

            // See QueryAsync (unlimited) above: skip cold rows already
            // present in InMemory by PK — hot wins.
            if (db.Set<T>().Find(entity.Id) is not null)
                continue;

            TryAttachCold(db, entity);

            hot.Add(entity);
        }

        return hot
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .OrderBy(e => e.CreatedAt)
            .ToList();
    }
}
