using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;

namespace SharpClaw.Infrastructure;

public static class InfrastructureServiceExtensions
{
    /// <summary>
    /// Registers the Infrastructure layer services for the given <see cref="StorageMode"/>.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        StorageMode mode,
        string? postgresConnectionString = null,
        Action<JsonFileOptions>? configureJsonFile = null)
    {
        switch (mode)
        {
            case StorageMode.JsonFile:
                var jsonOptions = new JsonFileOptions();
                configureJsonFile?.Invoke(jsonOptions);
                services.AddSingleton(jsonOptions);
                services.AddScoped<JsonFilePersistenceService>();
                services.AddDbContext<SharpClawDbContext>(options =>
                    options.UseInMemoryDatabase("SharpClaw"));
                break;

            case StorageMode.Postgres:
                ArgumentException.ThrowIfNullOrWhiteSpace(postgresConnectionString, nameof(postgresConnectionString));
                services.AddDbContext<SharpClawDbContext>(options =>
                    options.UseNpgsql(postgresConnectionString));
                break;
        }

        return services;
    }

    /// <summary>
    /// Initializes the infrastructure layer (e.g. loads persisted JSON data
    /// into the InMemory database). Call once after building the host.
    /// On the first run after a navigation-stripping fix, a full re-flush
    /// recompacts any bloated JSON files.
    /// </summary>
    public static async Task InitializeInfrastructureAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var jsonSync = scope.ServiceProvider.GetService<JsonFilePersistenceService>();
        if (jsonSync is not null)
        {
            await jsonSync.LoadAsync();
            await jsonSync.RecompactIfNeededAsync();
        }
    }
}
