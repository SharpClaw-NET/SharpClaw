using System.Linq.Expressions;
using SharpClaw.Contracts.Entities;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.INF.Persistence;


/// <summary>
/// Provider-neutral hint for <see cref="IPersistenceEntityResolver"/> queries.
/// Core services express the FK property name and value; the resolver decides
/// whether a cold index can satisfy the query more efficiently.
/// </summary>
public sealed record PersistenceQueryHint(string PropertyName, Guid Value);
