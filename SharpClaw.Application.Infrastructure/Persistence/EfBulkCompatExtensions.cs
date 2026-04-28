using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace SharpClaw.Infrastructure.Persistence;

/// <summary>
/// Provider-aware shims for EF Core bulk operations.
/// <para>
/// EF Core's <c>ExecuteDeleteAsync</c>/<c>ExecuteUpdateAsync</c> are translated by
/// the relational query pipeline and are <b>not supported by the InMemory provider</b>
/// (which we use in dev/test and JSON-backed mode).  Calling them throws
/// <see cref="InvalidOperationException"/> with
/// <c>"The methods 'ExecuteDelete' ... are not supported by the current database provider."</c>
/// </para>
/// <para>
/// These extensions detect the InMemory provider at runtime and fall back to
/// load + <c>RemoveRange</c> / per-entity property assignment.  On any other
/// provider they pass straight through to the native bulk operation, so
/// relational performance is unaffected.
/// </para>
/// </summary>
public static class EfBulkCompatExtensions
{
    private const string InMemoryProviderName = "Microsoft.EntityFrameworkCore.InMemory";

    private static bool IsInMemory(DbContext db)
        => string.Equals(db.Database.ProviderName, InMemoryProviderName, StringComparison.Ordinal);

    /// <summary>
    /// Provider-aware version of <see cref="EntityFrameworkQueryableExtensions.ExecuteDeleteAsync{TSource}(IQueryable{TSource}, CancellationToken)"/>.
    /// Falls back to load + <c>RemoveRange</c> + <c>SaveChangesAsync</c> on InMemory.
    /// </summary>
    public static async Task<int> ExecuteDeleteCompatAsync<T>(
        this IQueryable<T> source, DbContext db, CancellationToken ct = default)
        where T : class
    {
        if (!IsInMemory(db))
            return await source.ExecuteDeleteAsync(ct);

        var items = await source.ToListAsync(ct);
        if (items.Count == 0) return 0;

        db.Set<T>().RemoveRange(items);
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    /// <summary>
    /// Provider-aware version of <see cref="EntityFrameworkQueryableExtensions.ExecuteUpdateAsync{TSource}(IQueryable{TSource}, Expression{Func{SetPropertyCalls{TSource}, SetPropertyCalls{TSource}}}, CancellationToken)"/>.
    /// On InMemory the <paramref name="setPropertyCalls"/> expression cannot be
    /// translated, so callers must supply <paramref name="inMemoryApply"/> — a
    /// plain delegate that performs the equivalent assignment on a tracked entity.
    /// </summary>
    public static async Task<int> ExecuteUpdateCompatAsync<T>(
        this IQueryable<T> source,
        DbContext db,
        Action<UpdateSettersBuilder<T>> setPropertyCalls,
        Action<T> inMemoryApply,
        CancellationToken ct = default)
        where T : class
    {
        if (!IsInMemory(db))
            return await source.ExecuteUpdateAsync(setPropertyCalls, ct);

        var items = await source.ToListAsync(ct);
        if (items.Count == 0) return 0;

        foreach (var item in items)
            inMemoryApply(item);

        await db.SaveChangesAsync(ct);
        return items.Count;
    }
}
