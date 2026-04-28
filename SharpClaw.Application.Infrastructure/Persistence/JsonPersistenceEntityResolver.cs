using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
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
    ColdEntityStore coldStore) : IPersistenceEntityResolver
{
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
        if (db.Entry(cold).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            db.Attach(cold);

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

            if (db.Entry(entity).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
                db.Attach(entity);

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

            if (db.Entry(entity).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
                db.Attach(entity);

            hot.Add(entity);
        }

        return hot
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .OrderBy(e => e.CreatedAt)
            .ToList();
    }
}
