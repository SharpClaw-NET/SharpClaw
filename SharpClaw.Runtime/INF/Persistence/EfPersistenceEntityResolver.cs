using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Runtime.INF.Persistence;

/// <summary>
/// <see cref="IPersistenceEntityResolver"/> implementation for relational EF
/// providers. Delegates every operation directly to the active
/// <see cref="SharpClawDbContext"/>; no cold-storage fallback is applied.
/// </summary>
public sealed class EfPersistenceEntityResolver : IPersistenceEntityResolver
{
    public async Task<T?> FindAsync<T>(SharpClawDbContext db, Guid id, CancellationToken ct = default)
        where T : BaseEntity
        => await db.Set<T>().FindAsync([id], ct);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        SharpClawDbContext db,
        Expression<Func<T, bool>> predicate,
        PersistenceQueryHint? hint = null,
        CancellationToken ct = default)
        where T : BaseEntity
        => await db.Set<T>().Where(predicate).OrderBy(e => e.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        SharpClawDbContext db,
        Expression<Func<T, bool>> predicate,
        int limit,
        PersistenceQueryHint? hint = null,
        CancellationToken ct = default)
        where T : BaseEntity
        => await db.Set<T>()
            .Where(predicate)
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(ct);
}
