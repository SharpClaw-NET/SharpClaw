using Microsoft.EntityFrameworkCore;

namespace SharpClaw.Runtime.INF.Persistence.Registrations;

public sealed record RuntimeRegistrationDbContextRegistration(
    string SourceId,
    Type DbContextType,
    IReadOnlyList<Type> EntityTypes);

public sealed class RuntimeRegistrationDbContextRegistry
{
    private readonly Dictionary<Type, RuntimeRegistrationDbContextRegistration> _registrations = [];
    private readonly ReaderWriterLockSlim _lock = new();

    public void Register(RuntimeRegistrationDbContextRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!typeof(DbContext).IsAssignableFrom(registration.DbContextType))
            throw new ArgumentException(
                $"Type '{registration.DbContextType.FullName}' is not a DbContext.",
                nameof(registration));

        _lock.EnterWriteLock();
        try
        {
            _registrations[registration.DbContextType] = registration;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void UnregisterSource(string SourceId)
    {
        if (string.IsNullOrWhiteSpace(SourceId))
            throw new ArgumentException("Registration ID is required.", nameof(SourceId));

        _lock.EnterWriteLock();
        try
        {
            foreach (var contextType in _registrations
                         .Where(r => string.Equals(r.Value.SourceId, SourceId, StringComparison.Ordinal))
                         .Select(r => r.Key)
                         .ToArray())
            {
                _registrations.Remove(contextType);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool IsRegistered(Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);

        _lock.EnterReadLock();
        try
        {
            return _registrations.ContainsKey(dbContextType);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public RuntimeRegistrationDbContextRegistration? GetRegistration(Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);

        _lock.EnterReadLock();
        try
        {
            return _registrations.GetValueOrDefault(dbContextType);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IReadOnlyList<RuntimeRegistrationDbContextRegistration> GetAll()
    {
        _lock.EnterReadLock();
        try
        {
            return [.. _registrations.Values];
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
