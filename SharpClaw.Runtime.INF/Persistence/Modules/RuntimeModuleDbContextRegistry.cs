using Microsoft.EntityFrameworkCore;

namespace SharpClaw.Runtime.INF.Persistence.Modules;

public sealed record RuntimeModuleDbContextRegistration(
    string ModuleId,
    Type DbContextType,
    IReadOnlyList<Type> EntityTypes);

public sealed class RuntimeModuleDbContextRegistry
{
    private readonly Dictionary<Type, RuntimeModuleDbContextRegistration> _registrations = [];
    private readonly ReaderWriterLockSlim _lock = new();

    public void Register(RuntimeModuleDbContextRegistration registration)
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

    public void UnregisterModule(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
            throw new ArgumentException("Module ID is required.", nameof(moduleId));

        _lock.EnterWriteLock();
        try
        {
            foreach (var contextType in _registrations
                         .Where(r => string.Equals(r.Value.ModuleId, moduleId, StringComparison.Ordinal))
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

    public RuntimeModuleDbContextRegistration? GetRegistration(Type dbContextType)
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

    public IReadOnlyList<RuntimeModuleDbContextRegistration> GetAll()
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
