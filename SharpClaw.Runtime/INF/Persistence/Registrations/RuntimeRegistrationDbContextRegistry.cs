using Microsoft.EntityFrameworkCore;

namespace SharpClaw.Runtime.INF.Persistence.Registrations;


public sealed class RuntimeRegistrationDbContextRegistry
{
    private readonly Dictionary<Type, RuntimeRegistrationDbContextRegistration> _registrations = [];
    private readonly Lock _gate = new();

    public void Register(RuntimeRegistrationDbContextRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!typeof(DbContext).IsAssignableFrom(registration.DbContextType))
            throw new ArgumentException(
                $"Type '{registration.DbContextType.FullName}' is not a DbContext.",
                nameof(registration));

        lock (_gate)
        {
            _registrations[registration.DbContextType] = registration;
        }
    }

    public void UnregisterSource(string SourceId)
    {
        if (string.IsNullOrWhiteSpace(SourceId))
            throw new ArgumentException("Registration ID is required.", nameof(SourceId));

        lock (_gate)
        {
            foreach (var contextType in _registrations
                         .Where(r => string.Equals(r.Value.SourceId, SourceId, StringComparison.Ordinal))
                         .Select(r => r.Key)
                         .ToArray())
            {
                _registrations.Remove(contextType);
            }
        }
    }

    public bool IsRegistered(Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);

        lock (_gate)
        {
            return _registrations.ContainsKey(dbContextType);
        }
    }

    public RuntimeRegistrationDbContextRegistration? GetRegistration(Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);

        lock (_gate)
        {
            return _registrations.GetValueOrDefault(dbContextType);
        }
    }

    public IReadOnlyList<RuntimeRegistrationDbContextRegistration> GetAll()
    {
        lock (_gate)
        {
            return [.. _registrations.Values];
        }
    }
}
