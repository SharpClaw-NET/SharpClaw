using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Infrastructure.Persistence;

/// <summary>
/// Manages manual migration execution with request-draining via <see cref="MigrationGate"/>.
/// </summary>
public sealed class MigrationService(
    IServiceScopeFactory scopeFactory,
    MigrationGate gate,
    ILogger<MigrationService> logger) : IDisposable
{
    private readonly SemaphoreSlim _singleRun = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _singleRun.Dispose();

    /// <summary>
    /// Drains in-flight requests, applies pending EF Core migrations, and
    /// reopens the gate. Only one migration can run at a time.
    /// </summary>
    public async Task<MigrationResult> MigrateAsync(CancellationToken ct = default)
    {
        if (!await _singleRun.WaitAsync(0, ct))
            return MigrationResult.AlreadyRunning();

        try
        {
            logger.LogWarning("Migration requested. Draining in-flight requests...");
            using var migrationLock = await gate.EnterMigrationAsync(ct);
            logger.LogWarning("All requests drained. Applying migrations...");

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

            if (pending.Count == 0)
                return MigrationResult.NoPending();

            await db.Database.MigrateAsync(ct);
            logger.LogWarning("Applied {Count} migration(s): {Names}",
                pending.Count, string.Join(", ", pending));

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
    /// </summary>
    public async Task<MigrationStatusResult> GetStatusAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        return new(gate.State, applied, pending);
    }
}

/// <summary>Result of a migration attempt.</summary>
public record MigrationResult(bool Applied, bool AlreadyInProgress, IReadOnlyList<string> Migrations, string Message)
{
    public static MigrationResult Success(List<string> names) => new(true, false, names, $"Applied {names.Count} migration(s).");
    public static MigrationResult NoPending() => new(false, false, [], "No pending migrations.");
    public static MigrationResult AlreadyRunning() => new(false, true, [], "A migration is already in progress.");
}

/// <summary>Current migration status snapshot.</summary>
public record MigrationStatusResult(MigrationState State, IReadOnlyList<string> Applied, IReadOnlyList<string> Pending);
