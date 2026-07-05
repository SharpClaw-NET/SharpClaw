using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using JSONColdStore;

using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Runtime.INF.Persistence.Modules;

public sealed class ModuleDbContextFactory(
    RuntimeModuleDbContextRegistry registry,
    ModuleDbContextOptions options,
    DatabaseProviderOptions? databaseOptions = null,
    ILoggerFactory? loggerFactory = null,
    IServiceProvider? serviceProvider = null) : IModuleDbContextFactory
{
    public object CreateDbContext(Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);

        if (!typeof(DbContext).IsAssignableFrom(dbContextType))
            throw new ArgumentException(
                $"Type '{dbContextType.FullName}' is not a DbContext.",
                nameof(dbContextType));

        if (!registry.IsRegistered(dbContextType))
            throw new InvalidOperationException(
                $"Module DbContext '{dbContextType.FullName}' is not registered.");

        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(
            typeof(DbContextOptionsBuilder<>).MakeGenericType(dbContextType))!;
        ConfigureOptions(builder, dbContextType);

        var contextOptions = builder.Options;
        var context = Activator.CreateInstance(dbContextType, contextOptions)
            ?? throw new InvalidOperationException(
                $"Failed to create module DbContext '{dbContextType.FullName}'.");

        return context;
    }

    private void ConfigureOptions(DbContextOptionsBuilder builder, Type dbContextType)
    {
        var providerOptions = databaseOptions
            ?? new DatabaseProviderOptions
            {
                Provider = options.StorageMode,
                ConnectionString = options.ConnectionString,
            };
        providerOptions.Validate();

        if (loggerFactory is not null)
            builder.UseLoggerFactory(loggerFactory);

        if (providerOptions.EnableDetailedErrors)
            builder.EnableDetailedErrors();

        if (providerOptions.EnableSensitiveDataLogging)
            builder.EnableSensitiveDataLogging();

        switch (providerOptions.Provider)
        {
            case StorageMode.JsonFile:
                var jsonOptions = serviceProvider?.GetRequiredService<JsonColdStoreStorageOptions>()
                    ?? databaseOptions?.JsonFile
                    ?? throw new InvalidOperationException(
                        "JSONColdStore options are not registered for module persistence.");
                builder.UseJsonColdStoreDatabase(
                    JsonColdStoreRegistration.GetModuleDirectory(jsonOptions, dbContextType),
                    store => JsonColdStoreRegistration.ConfigureStore(
                        store,
                        jsonOptions,
                        jsonOptions.EncryptAtRest
                            ? serviceProvider?.GetRequiredService<JsonColdStoreEncryptionKey>()
                                ?? throw new InvalidOperationException(
                                    "JSONColdStore encryption is enabled, but no service provider is available for module persistence.")
                            : null));
                break;
            case StorageMode.Postgres:
                RequireConnectionString(providerOptions);
                var postgres = providerOptions.Postgres;
                builder.UseNpgsql(providerOptions.ConnectionString, npgsql =>
                {
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
                break;
            case StorageMode.SqlServer:
                RequireConnectionString(providerOptions);
                var sqlServer = providerOptions.SqlServer;
                builder.UseSqlServer(providerOptions.ConnectionString, builder =>
                {
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
                break;
            case StorageMode.SQLite:
                RequireConnectionString(providerOptions);
                var sqlite = providerOptions.SQLite;
                builder.UseSqlite(providerOptions.ConnectionString, builder =>
                {
                    if (sqlite.CommandTimeoutSeconds is { } timeout)
                        builder.CommandTimeout(timeout);
                });
                break;
            case StorageMode.MySql:
                throw new NotSupportedException(
                    "MySQL/MariaDB support requires Pomelo.EntityFrameworkCore.MySql with EFC 10 compatibility. Not yet available.");
            case StorageMode.Oracle:
                throw new NotSupportedException(
                    "Oracle support requires Oracle.EntityFrameworkCore with EFC 10 compatibility. Not yet available.");
            default:
                throw new InvalidOperationException($"Unsupported storage mode '{providerOptions.Provider}'.");
        }
    }

    private static void RequireConnectionString(DatabaseProviderOptions providerOptions)
    {
        if (string.IsNullOrWhiteSpace(providerOptions.ConnectionString))
            throw new InvalidOperationException(
                $"A connection string is required for module persistence when Database:Provider is '{providerOptions.Provider}'.");
    }
}
