using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using JSONColdStore;

using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Infrastructure.Persistence.Modules;

public sealed class ModuleDbContextFactory(
    RuntimeModuleDbContextRegistry registry,
    ModuleDbContextOptions options,
    IConfiguration? configuration = null,
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
        if (loggerFactory is not null)
            builder.UseLoggerFactory(loggerFactory);

        var enableDetailedErrors = configuration is null
            || !bool.TryParse(configuration["Database:EnableDetailedErrors"], out var detailedErrors)
            || detailedErrors;

        var enableSensitiveDataLogging = configuration is not null
            && bool.TryParse(configuration["Database:EnableSensitiveDataLogging"], out var sensitiveDataLogging)
            && sensitiveDataLogging;

        if (enableDetailedErrors)
            builder.EnableDetailedErrors();

        if (enableSensitiveDataLogging)
            builder.EnableSensitiveDataLogging();

        switch (options.StorageMode)
        {
            case StorageMode.JsonFile:
                var jsonOptions = serviceProvider?.GetRequiredService<JsonColdStoreStorageOptions>()
                    ?? throw new InvalidOperationException(
                        "JSONColdStore options are not registered for module persistence.");
                builder.UseJsonColdStoreDatabase(
                    JsonColdStoreRegistration.GetModuleDirectory(jsonOptions, dbContextType),
                    store => JsonColdStoreRegistration.ConfigureStore(
                        store,
                        jsonOptions,
                        jsonOptions.EncryptAtRest
                            ? serviceProvider.GetRequiredService<JsonColdStoreEncryptionKey>()
                            : null));
                break;
            case StorageMode.Postgres:
                RequireConnectionString();
                builder.UseNpgsql(options.ConnectionString);
                break;
            case StorageMode.SqlServer:
                RequireConnectionString();
                builder.UseSqlServer(options.ConnectionString);
                break;
            case StorageMode.SQLite:
                RequireConnectionString();
                builder.UseSqlite(options.ConnectionString);
                break;
            case StorageMode.MySql:
                throw new NotSupportedException(
                    "MySQL/MariaDB support requires Pomelo.EntityFrameworkCore.MySql with EFC 10 compatibility. Not yet available.");
            case StorageMode.Oracle:
                throw new NotSupportedException(
                    "Oracle support requires Oracle.EntityFrameworkCore with EFC 10 compatibility. Not yet available.");
            default:
                throw new InvalidOperationException($"Unsupported storage mode '{options.StorageMode}'.");
        }
    }

    private void RequireConnectionString()
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException(
                $"A connection string is required for module persistence when Database:Provider is '{options.StorageMode}'.");
    }
}
