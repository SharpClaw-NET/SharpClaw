using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.INF.Persistence;

/// <summary>
/// Manages manual migration execution with request-draining via <see cref="MigrationGate"/>.
/// </summary>
public sealed class MigrationService(
    IServiceScopeFactory scopeFactory,
    MigrationGate gate,
    ILogger<MigrationService> logger) : IDisposable
{
    private readonly SemaphoreSlim _singleRun = new(1, 1);
    private static readonly Action<ILogger, Exception?> LogDraining = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(LogDraining)),
        "Migration requested. Draining in-flight requests...");
    private static readonly Action<ILogger, Exception?> LogApplying = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2, nameof(LogApplying)),
        "All requests drained. Applying migrations...");
    private static readonly Action<ILogger, int, string, Exception?> LogApplied = LoggerMessage.Define<int, string>(
        LogLevel.Warning, new EventId(3, nameof(LogApplied)),
        "Applied {Count} migration(s): {Names}");

    /// <inheritdoc />
    public void Dispose() => _singleRun.Dispose();

    /// <summary>
    /// Drains in-flight requests, applies pending EF Core migrations, and
    /// reopens the gate. Only one migration can run at a time.
    /// </summary>
    public async Task<MigrationResult> MigrateAsync(CancellationToken ct = default)
    {
        if (!await _singleRun.WaitAsync(0, ct).ConfigureAwait(false))
            return MigrationResult.AlreadyRunning();

        try
        {
            LogDraining(logger, null);
            using var migrationLock = await gate.EnterMigrationAsync(ct).ConfigureAwait(false);
            LogApplying(logger, null);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            if (!db.Database.IsRelational())
                return MigrationResult.NoPending();

            var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();

            if (pending.Count == 0)
                return MigrationResult.NoPending();

            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Warning))
                LogApplied(logger, pending.Count, string.Join(", ", pending), null);

            return MigrationResult.Success(pending);
            // Dispose releases gate → requests resume.
        }
        finally
        {
            _singleRun.Release();
        }
    }

    /// <summary>
    /// Returns the current migration gate state plus applied/pending migration lists.
    /// Returns empty lists for non-relational providers (e.g. InMemory/JsonFile).
    /// </summary>
    public async Task<MigrationStatusResult> GetStatusAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        if (!db.Database.IsRelational())
            return new(gate.State, [], []);

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        return new(gate.State, applied, pending);
    }
}
