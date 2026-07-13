using System.Reflection;

using Microsoft.EntityFrameworkCore;

namespace SharpClaw.Runtime.INF.Persistence.Modules;

public sealed class ModulePersistenceRegistrationFactory
{
    public IReadOnlyList<RuntimeModuleDbContextRegistration> CreateRegistrations(
        string moduleId,
        Assembly assembly)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
            throw new ArgumentException("Module ID is required.", nameof(moduleId));
        ArgumentNullException.ThrowIfNull(assembly);

        return assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && typeof(DbContext).IsAssignableFrom(t))
            .Select(t => new RuntimeModuleDbContextRegistration(
                moduleId,
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
