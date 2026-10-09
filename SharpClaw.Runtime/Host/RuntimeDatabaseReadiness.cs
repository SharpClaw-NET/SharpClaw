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
        var scope = scopeFactory.CreateAsyncScope();
        await using var scopeAsyncDisposal = scope.ConfigureAwait(false);
        var dbContext = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        await selection.Provider.InitializeAsync(dbContext, cancellationToken).ConfigureAwait(false);

        if (!await dbContext.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The configured {persistenceOptions.ProviderKey} database is not ready.");
        }
    }
}
