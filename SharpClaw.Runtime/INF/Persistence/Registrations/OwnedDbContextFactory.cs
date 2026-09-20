using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Persistence;

namespace SharpClaw.Runtime.INF.Persistence.Registrations;

public sealed class RegistrationDbContextFactory(
    RuntimeRegistrationDbContextRegistry registry,
    PersistenceProviderSelection selection,
    SharpClawPersistenceOptions persistenceOptions,
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    ILoggerFactory? loggerFactory = null) : IOwnedDbContextFactory
{
    public object CreateDbContext(Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);
        if (!typeof(DbContext).IsAssignableFrom(dbContextType))
            throw new ArgumentException($"Type '{dbContextType.FullName}' is not a DbContext.", nameof(dbContextType));
        if (!registry.IsRegistered(dbContextType))
            throw new InvalidOperationException($"Registration DbContext '{dbContextType.FullName}' is not registered.");

        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(
            typeof(DbContextOptionsBuilder<>).MakeGenericType(dbContextType))!;
        if (loggerFactory is not null)
            builder.UseLoggerFactory(loggerFactory);
        if (persistenceOptions.EnableDetailedErrors)
            builder.EnableDetailedErrors();
        if (persistenceOptions.EnableSensitiveDataLogging)
            builder.EnableSensitiveDataLogging();

        selection.Provider.Configure(
            builder,
            new SharpClawPersistenceProviderContext(
                serviceProvider,
                configuration,
                persistenceOptions,
                dbContextType,
                UseMigrations: false));

        return Activator.CreateInstance(dbContextType, builder.Options)
            ?? throw new InvalidOperationException($"Failed to create registration DbContext '{dbContextType.FullName}'.");
    }
}
