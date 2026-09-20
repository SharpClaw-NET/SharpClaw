using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Persistence;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Runtime.INF.Persistence.Registrations;

namespace SharpClaw.Runtime.INF;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        SharpClawPersistenceOptions persistenceOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(persistenceOptions);
        persistenceOptions.Validate();

        services.AddSingleton(configuration);
        services.AddSingleton(persistenceOptions);
        services.AddSingleton<PersistenceProviderSelection>(serviceProvider =>
            new(SharpClawPersistenceProviderResolver.Resolve(
                serviceProvider.GetServices<ISharpClawPersistenceProvider>(),
                persistenceOptions.ProviderKey)));
        services.AddSingleton<RuntimeRegistrationDbContextRegistry>();
        services.AddSingleton<RegistrationPersistenceRegistrationFactory>();
        services.AddSingleton<IOwnedDbContextFactory, RegistrationDbContextFactory>();

        services.AddDbContext<SharpClawDbContext>((serviceProvider, optionsBuilder) =>
        {
            ConfigureCommonOptions(serviceProvider, optionsBuilder, persistenceOptions);
            var provider = serviceProvider.GetRequiredService<PersistenceProviderSelection>().Provider;
            provider.Configure(
                optionsBuilder,
                new SharpClawPersistenceProviderContext(
                    serviceProvider,
                    configuration,
                    persistenceOptions,
                    typeof(SharpClawDbContext),
                    UseMigrations: true));
        });
        services.AddScoped<IPersistenceEntityResolver, EfPersistenceEntityResolver>();
        services.AddSingleton<MigrationGate>();
        services.AddSingleton<MigrationService>();
        services.AddScoped<ISharpClawDataContext>(
            serviceProvider => serviceProvider.GetRequiredService<SharpClawDbContext>());

        return services;
    }

    private static void ConfigureCommonOptions(
        IServiceProvider serviceProvider,
        DbContextOptionsBuilder optionsBuilder,
        SharpClawPersistenceOptions persistenceOptions)
    {
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        if (loggerFactory is not null)
            optionsBuilder.UseLoggerFactory(loggerFactory);
        if (persistenceOptions.EnableDetailedErrors)
            optionsBuilder.EnableDetailedErrors();
        if (persistenceOptions.EnableSensitiveDataLogging)
            optionsBuilder.EnableSensitiveDataLogging();
    }

    public static async Task InitializeInfrastructureAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var provider = scope.ServiceProvider.GetRequiredService<PersistenceProviderSelection>().Provider;
        await provider.InitializeAsync(dbContext, CancellationToken.None);
    }

    public static Task ShutdownInfrastructureAsync(this IServiceProvider services)
    {
        services.GetService<MigrationGate>()?.Dispose();
        (services.GetService<MigrationService>() as IDisposable)?.Dispose();
        return Task.CompletedTask;
    }
}

public sealed record PersistenceProviderSelection(ISharpClawPersistenceProvider Provider);
