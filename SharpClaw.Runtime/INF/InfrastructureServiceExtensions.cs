using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Runtime.INF.Persistence.Modules;

namespace SharpClaw.Runtime.INF;

public static class InfrastructureServiceExtensions
{
    /// <summary>
    /// Registers the Infrastructure layer services for the given <see cref="StorageMode"/>.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        DatabaseProviderOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(databaseOptions);
        databaseOptions.Validate();

        services.AddSingleton(new ModuleDbContextOptions
        {
            StorageMode = databaseOptions.Provider,
            ConnectionString = databaseOptions.ConnectionString,
        });
        services.AddSingleton(databaseOptions);
        services.AddSingleton<RuntimeModuleDbContextRegistry>();
        services.AddSingleton<ModulePersistenceRegistrationFactory>();
        services.AddSingleton<IModuleDbContextFactory, ModuleDbContextFactory>();

        switch (databaseOptions.Provider)
        {
            case StorageMode.JsonFile:
                var jsonOptions = databaseOptions.JsonFile;
                services.AddSingleton(jsonOptions);
                if (jsonOptions.EncryptAtRest)
                {
                    services.AddSingleton(sp =>
                        JsonColdStoreEncryptionKey.FromBytes(
                            sp.GetRequiredService<EncryptionOptions>().Key));
                }

                services.AddDbContext<SharpClawDbContext>((sp, options) =>
                {
                    ConfigureCommonOptions(sp, options, databaseOptions);
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
                RequireConnectionString(databaseOptions.ConnectionString, databaseOptions.Provider);
                services.AddDbContext<SharpClawDbContext>((sp, options) =>
                {
                    ConfigureCommonOptions(sp, options, databaseOptions);
                    var postgres = databaseOptions.Postgres;
                    options.UseNpgsql(databaseOptions.ConnectionString, npgsql =>
                    {
                        npgsql.MigrationsAssembly(postgres.MigrationsAssembly);
                        if (postgres.CommandTimeoutSeconds is { } timeout)
                            npgsql.CommandTimeout(timeout);
                        if (postgres.EnableRetryOnFailure)
                        {
                            npgsql.EnableRetryOnFailure(
                                postgres.MaxRetryCount,
                                TimeSpan.FromSeconds(postgres.MaxRetryDelaySeconds),
                                errorCodesToAdd: null);
                        }
                    });
                });
                services.AddScoped<IPersistenceEntityResolver, EfPersistenceEntityResolver>();
                break;

            case StorageMode.SqlServer:
                RequireConnectionString(databaseOptions.ConnectionString, databaseOptions.Provider);
                services.AddDbContext<SharpClawDbContext>((sp, options) =>
                {
                    ConfigureCommonOptions(sp, options, databaseOptions);
                    var sqlServer = databaseOptions.SqlServer;
                    options.UseSqlServer(databaseOptions.ConnectionString, builder =>
                    {
                        builder.MigrationsAssembly(sqlServer.MigrationsAssembly);
                        if (sqlServer.CommandTimeoutSeconds is { } timeout)
                            builder.CommandTimeout(timeout);
                        if (sqlServer.EnableRetryOnFailure)
                        {
                            builder.EnableRetryOnFailure(
                                sqlServer.MaxRetryCount,
                                TimeSpan.FromSeconds(sqlServer.MaxRetryDelaySeconds),
                                errorNumbersToAdd: null);
                        }
                    });
                });
                services.AddScoped<IPersistenceEntityResolver, EfPersistenceEntityResolver>();
                break;

            case StorageMode.SQLite:
                RequireConnectionString(databaseOptions.ConnectionString, databaseOptions.Provider);
                services.AddDbContext<SharpClawDbContext>((sp, options) =>
                {
                    ConfigureCommonOptions(sp, options, databaseOptions);
                    var sqlite = databaseOptions.SQLite;
                    options.UseSqlite(databaseOptions.ConnectionString, builder =>
                    {
                        builder.MigrationsAssembly(sqlite.MigrationsAssembly);
                        if (sqlite.CommandTimeoutSeconds is { } timeout)
                            builder.CommandTimeout(timeout);
                    });
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

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        StorageMode mode,
        string? connectionString = null,
        Action<JsonColdStoreStorageOptions>? configureJsonColdStore = null)
    {
        var databaseOptions = new DatabaseProviderOptions
        {
            Provider = mode,
            ConnectionString = mode == StorageMode.JsonFile ? null : connectionString,
        };
        configureJsonColdStore?.Invoke(databaseOptions.JsonFile);

        return services.AddInfrastructure(databaseOptions);
    }

    private static void RequireConnectionString(string? cs, StorageMode mode)
    {
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"ConnectionStrings:{mode} is required when Database:Provider is '{mode}'. " +
                $"Set it in the .env file or environment variables.");
    }

    private static void ConfigureCommonOptions(
        IServiceProvider serviceProvider,
        DbContextOptionsBuilder options,
        DatabaseProviderOptions databaseOptions)
    {
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        if (loggerFactory is not null)
            options.UseLoggerFactory(loggerFactory);

        if (databaseOptions.EnableDetailedErrors)
            options.EnableDetailedErrors();

        if (databaseOptions.EnableSensitiveDataLogging)
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
