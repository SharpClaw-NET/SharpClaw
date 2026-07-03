using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.Modules;

namespace SharpClaw.Infrastructure;

public static class InfrastructureServiceExtensions
{
    /// <summary>
    /// Registers the Infrastructure layer services for the given <see cref="StorageMode"/>.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        StorageMode mode,
        string? connectionString = null,
        Action<JsonColdStoreStorageOptions>? configureJsonColdStore = null)
    {
        services.AddSingleton(new ModuleDbContextOptions
        {
            StorageMode = mode,
            ConnectionString = connectionString,
        });
        services.AddSingleton<RuntimeModuleDbContextRegistry>();
        services.AddSingleton<ModulePersistenceRegistrationFactory>();
        services.AddSingleton<IModuleDbContextFactory, ModuleDbContextFactory>();

        switch (mode)
        {
            case StorageMode.JsonFile:
                var jsonOptions = new JsonColdStoreStorageOptions();
                configureJsonColdStore?.Invoke(jsonOptions);
                services.AddSingleton(jsonOptions);
                if (jsonOptions.EncryptAtRest)
                {
                    services.AddSingleton(sp =>
                        JsonColdStoreEncryptionKey.FromBytes(
                            sp.GetRequiredService<EncryptionOptions>().Key));
                }

                services.AddDbContext<SharpClawDbContext>((sp, options) =>
                {
                    ConfigureLogging(sp, options);
                    options.UseJsonColdStoreDatabase(
                        jsonOptions.DataDirectory,
                        store => JsonColdStoreRegistration.ConfigureStore(
                            store,
                            jsonOptions,
                            jsonOptions.EncryptAtRest
                                ? sp.GetRequiredService<JsonColdStoreEncryptionKey>()
                                : null));
                });
                services.AddScoped<IPersistenceEntityResolver, EfPersistenceEntityResolver>();
                break;

            case StorageMode.Postgres:
                RequireConnectionString(connectionString, mode);
                services.AddDbContext<SharpClawDbContext>((sp, options) =>
                {
                    ConfigureLogging(sp, options);
                    options.UseNpgsql(connectionString, npgsql =>
                        npgsql.MigrationsAssembly("SharpClaw.Migrations.Postgres"));
                });
                services.AddScoped<IPersistenceEntityResolver, EfPersistenceEntityResolver>();
                break;

            case StorageMode.SqlServer:
                RequireConnectionString(connectionString, mode);
                services.AddDbContext<SharpClawDbContext>((sp, options) =>
                {
                    ConfigureLogging(sp, options);
                    options.UseSqlServer(connectionString, sqlServer =>
                        sqlServer.MigrationsAssembly("SharpClaw.Migrations.SqlServer"));
                });
                services.AddScoped<IPersistenceEntityResolver, EfPersistenceEntityResolver>();
                break;

            case StorageMode.SQLite:
                RequireConnectionString(connectionString, mode);
                services.AddDbContext<SharpClawDbContext>((sp, options) =>
                {
                    ConfigureLogging(sp, options);
                    options.UseSqlite(connectionString, sqlite =>
                        sqlite.MigrationsAssembly("SharpClaw.Migrations.SQLite"));
                });
                services.AddScoped<IPersistenceEntityResolver, EfPersistenceEntityResolver>();
                break;

            case StorageMode.MySql:
                throw new NotSupportedException(
                    "MySQL/MariaDB support requires Pomelo.EntityFrameworkCore.MySql " +
                    "with EFC 10 compatibility. Not yet available.");

            case StorageMode.Oracle:
                throw new NotSupportedException(
                    "Oracle support requires Oracle.EntityFrameworkCore " +
                    "with EFC 10 compatibility. Not yet available.");
        }

        services.AddSingleton<MigrationGate>();
        services.AddSingleton<MigrationService>();
        services.AddScoped<ICoreEntityIdProvider, CoreEntityIdProvider>();
        services.AddScoped<ISharpClawDataContext>(
            sp => sp.GetRequiredService<SharpClawDbContext>());

        return services;
    }

    private static void RequireConnectionString(string? cs, StorageMode mode)
    {
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"ConnectionStrings:{mode} is required when Database:Provider is '{mode}'. " +
                $"Set it in the .env file or environment variables.");
    }

    private static void ConfigureLogging(
        IServiceProvider serviceProvider,
        DbContextOptionsBuilder options)
    {
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        if (loggerFactory is not null)
            options.UseLoggerFactory(loggerFactory);

        var configuration = serviceProvider.GetService<IConfiguration>();

        var enableDetailedErrors = configuration is null
            || !bool.TryParse(configuration["Database:EnableDetailedErrors"], out var detailedErrors)
            || detailedErrors;

        var enableSensitiveDataLogging = configuration is not null
            && bool.TryParse(configuration["Database:EnableSensitiveDataLogging"], out var sensitiveDataLogging)
            && sensitiveDataLogging;

        if (enableDetailedErrors)
            options.EnableDetailedErrors();

        if (enableSensitiveDataLogging)
            options.EnableSensitiveDataLogging();
    }

    /// <summary>
    /// Initializes infrastructure services after the host is built.
    /// </summary>
    public static async Task InitializeInfrastructureAsync(this IServiceProvider services)
    {
        var storage = services.GetRequiredService<ModuleDbContextOptions>();
        if (storage.StorageMode != StorageMode.JsonFile)
            return;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        await db.Database.CanConnectAsync(CancellationToken.None);
    }

    /// <summary>
    /// Gracefully shuts down infrastructure services owned by SharpClaw.
    /// </summary>
    public static async Task ShutdownInfrastructureAsync(this IServiceProvider services)
    {
        services.GetService<MigrationGate>()?.Dispose();
        (services.GetService<MigrationService>() as IDisposable)?.Dispose();
        await Task.CompletedTask;
    }
}
