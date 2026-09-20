using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Persistence;
using SharpClaw.Runtime.INF;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.Host;

internal sealed class RuntimeDatabaseReadiness(
    IServiceScopeFactory scopeFactory,
    PersistenceProviderSelection selection,
    SharpClawPersistenceOptions persistenceOptions)
{
    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        await selection.Provider.InitializeAsync(dbContext, cancellationToken);

        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"The configured {persistenceOptions.ProviderKey} database is not ready.");
        }
    }
}
