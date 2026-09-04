using System.Reflection;

using Microsoft.EntityFrameworkCore;

namespace SharpClaw.Runtime.INF.Persistence.Registrations;

public sealed class RegistrationPersistenceRegistrationFactory
{
    public IReadOnlyList<RuntimeRegistrationDbContextRegistration> CreateRegistrations(
        string SourceId,
        Assembly assembly)
    {
        if (string.IsNullOrWhiteSpace(SourceId))
            throw new ArgumentException("Registration ID is required.", nameof(SourceId));
        ArgumentNullException.ThrowIfNull(assembly);

        return assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && typeof(DbContext).IsAssignableFrom(t))
            .Select(t => new RuntimeRegistrationDbContextRegistration(
                SourceId,
                t,
                GetEntityTypes(t)))
            .ToList();
    }

    private static IReadOnlyList<Type> GetEntityTypes(Type dbContextType)
    {
        return dbContextType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .Distinct()
            .ToList();
    }
}
