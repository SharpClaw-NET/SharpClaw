using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.Host;

/// <summary>Validates the selected database before Runtime discovery becomes visible.</summary>
internal sealed class RuntimeDatabaseReadiness(
    IServiceScopeFactory scopeFactory,
    DatabaseProviderOptions databaseOptions)
{
    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        if (databaseOptions.Provider == StorageMode.JsonFile)
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"The configured {databaseOptions.Provider} database is not ready.");
        }

        if (databaseOptions.Provider == StorageMode.JsonFile)
        {
            return;
        }
    }
}
