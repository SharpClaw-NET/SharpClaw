using System.Linq.Expressions;
using SharpClaw.Contracts.Entities;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Infrastructure.Persistence;

/// <summary>
/// Provider-neutral hint for <see cref="IPersistenceEntityResolver"/> queries.
/// Core services express the FK property name and value; the resolver decides
/// whether a cold index can satisfy the query more efficiently.
/// </summary>
public sealed record PersistenceQueryHint(string PropertyName, Guid Value);

/// <summary>
/// Provider-neutral abstraction for entity lookup and query that works for
/// every configured storage provider.
/// <para>
/// Implementations delegate through the configured EF provider. Core services
/// use this interface when they need app-owned entity resolution without
/// knowing which provider backs the DbContext.
/// </para>
/// </summary>
public interface IPersistenceEntityResolver
{
    /// <summary>
    /// Finds a single entity by primary key. Returns <c>null</c> when not found.
    /// Implementations may attach the returned entity to <paramref name="db"/>
    /// when the configured provider requires it.
    /// </summary>
    Task<T?> FindAsync<T>(SharpClawDbContext db, Guid id, CancellationToken ct = default)
        where T : BaseEntity;

    /// <summary>
    /// Returns all entities matching <paramref name="predicate"/>, ordered
    /// chronologically. An optional <paramref name="hint"/> may guide
    /// resolver implementations to an indexed lookup path.
    /// </summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(
        SharpClawDbContext db,
        Expression<Func<T, bool>> predicate,
        PersistenceQueryHint? hint = null,
        CancellationToken ct = default)
        where T : BaseEntity;

    /// <summary>
    /// Returns up to <paramref name="limit"/> entities matching
    /// <paramref name="predicate"/>, ordered by most-recent first then
    /// re-ordered chronologically. An optional <paramref name="hint"/>
    /// may guide resolver implementations to an indexed lookup path.
    /// </summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(
        SharpClawDbContext db,
        Expression<Func<T, bool>> predicate,
        int limit,
        PersistenceQueryHint? hint = null,
        CancellationToken ct = default)
        where T : BaseEntity;
}
