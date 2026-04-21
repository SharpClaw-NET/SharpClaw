using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        string? connectionString = null,
        Action<JsonFileOptions>? configureJsonFile = null)
    {
        switch (mode)
        {
            case StorageMode.JsonFile:
                var jsonOptions = new JsonFileOptions();
                configureJsonFile?.Invoke(jsonOptions);
                services.AddSingleton(jsonOptions);
                services.AddSingleton<IPersistenceFileSystem, PhysicalPersistenceFileSystem>();
                services.AddSingleton<DirectoryLockManager>();
                services.AddSingleton<TransactionQueue>();
                services.AddScoped<JsonFilePersistenceService>();
                services.AddSingleton<ColdEntityStore>();
                services.AddSingleton<ColdIndexMaintenanceService>();

                services.AddSingleton<FlushQueue>(sp => 
                    new FlushQueue(sp.GetRequiredService<ILogger<FlushQueue>>(), capacity: 256));
                services.AddSingleton<SnapshotService>(sp => 
                    new SnapshotService(
                        sp.GetRequiredService<IPersistenceFileSystem>(),
                        sp.GetRequiredService<JsonFileOptions>(),
                        sp.GetRequiredService<DirectoryLockManager>(),
                        sp.GetRequiredService<ILogger<SnapshotService>>(),
                        sp.GetRequiredService<FlushQueue>()));
                services.AddSingleton<ISnapshotFallback>(sp => sp.GetRequiredService<SnapshotService>());
                services.AddSingleton<FlushWorker>(sp =>
                    new FlushWorker(
                        sp.GetRequiredService<FlushQueue>(),
                        sp,
                        sp.GetRequiredService<ILogger<FlushWorker>>()));
                services.AddSingleton<JsonPersistenceHealthCheck>();
                services.AddSingleton<EntityMigrationRegistry>();

                services.AddDbContext<SharpClawDbContext>(options =>
                    options.UseInMemoryDatabase("SharpClaw"));
                break;

            case StorageMode.Postgres:
                RequireConnectionString(connectionString, mode);
                services.AddDbContext<SharpClawDbContext>(options =>
                    options.UseNpgsql(connectionString, npgsql =>
                        npgsql.MigrationsAssembly("SharpClaw.Migrations.Postgres")));
                break;

            case StorageMode.SqlServer:
                RequireConnectionString(connectionString, mode);
                services.AddDbContext<SharpClawDbContext>(options =>
                    options.UseSqlServer(connectionString, sqlServer =>
                        sqlServer.MigrationsAssembly("SharpClaw.Migrations.SqlServer")));
                break;

            case StorageMode.SQLite:
                RequireConnectionString(connectionString, mode);
                services.AddDbContext<SharpClawDbContext>(options =>
                    options.UseSqlite(connectionString, sqlite =>
                        sqlite.MigrationsAssembly("SharpClaw.Migrations.SQLite")));
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

        // Register migration gate and service for relational providers only.
        if (mode != StorageMode.JsonFile)
        {
            services.AddSingleton<MigrationGate>();
            services.AddSingleton<MigrationService>();
        }

        return services;
    }

    private static void RequireConnectionString(string? cs, StorageMode mode)
    {
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"ConnectionStrings:{mode} is required when Database:Provider is '{mode}'. " +
                $"Set it in the .env file or environment variables.");
    }

    /// <summary>
    /// Initializes the infrastructure layer (e.g. loads persisted JSON data
    /// into the InMemory database). Call once after building the host.
    /// On the first run after a navigation-stripping fix, a full re-flush
    /// recompacts any bloated JSON files.
    /// </summary>
    public static async Task InitializeInfrastructureAsync(this IServiceProvider services)
    {
        // Phase C: Recover any incomplete two-phase commits before anything else.
        var jsonOpts = services.GetService<JsonFileOptions>();
        var fsys = services.GetService<IPersistenceFileSystem>();
        if (jsonOpts is not null && fsys is not null)
        {
            await TwoPhaseCommit.RecoverAllAsync(fsys, jsonOpts.DataDirectory, CancellationToken.None);
        }

        // Phase A′: Replay any pending transaction manifests before loading.
        var txQueue = services.GetService<TransactionQueue>();
        if (txQueue is not null)
        {
            using var replayScope = services.CreateScope();
            var replaySync = replayScope.ServiceProvider.GetService<JsonFilePersistenceService>();
            if (replaySync is not null)
            {
                var replayed = await txQueue.ReplayPendingAsync(replaySync.ReplayManifestAsync);
                if (replayed > 0)
                {
                    var logger = replayScope.ServiceProvider.GetService<ILogger<TransactionQueue>>();
                    logger?.LogInformation("Replayed {Count} pending transaction(s) on startup", replayed);
                }
            }
        }

        using var scope = services.CreateScope();
        var jsonSync = scope.ServiceProvider.GetService<JsonFilePersistenceService>();
        if (jsonSync is not null)
        {
            await jsonSync.LoadAsync();

            // Phase J: Startup full-scan checksum verification.
            // Runs before index rebuild so corrupted files are quarantined
            // before they can pollute the indexes.
            if (jsonOpts!.EnableChecksums && fsys is not null)
            {
                var checksumLogger = scope.ServiceProvider.GetRequiredService<ILogger<ColdEntityStore>>();
                var encOpts = scope.ServiceProvider.GetService<EncryptionOptions>();
                var hmacKey = encOpts?.Key;
                if (hmacKey is { Length: > 0 })
                {
                    var entityDirs = fsys.DirectoryExists(jsonOpts.DataDirectory)
                        ? fsys.GetDirectories(jsonOpts.DataDirectory)
                        : [];
                    foreach (var entityDir in entityDirs)
                    {
                        var mismatched = await ChecksumManifest.VerifyAllAsync(
                            fsys, entityDir, hmacKey, checksumLogger, CancellationToken.None);
                        foreach (var file in mismatched)
                        {
                            QuarantineService.MoveToQuarantine(fsys, file, entityDir, checksumLogger);
                        }

                        if (mismatched.Count > 0)
                        {
                            await ChecksumManifest.RebuildManifestAsync(
                                fsys, entityDir, hmacKey, jsonOpts.FsyncOnWrite, checksumLogger, CancellationToken.None);
                        }
                    }
                }
            }

            // Phase D: Parallel cold index rebuild + FK validation.
            await jsonSync.RebuildColdIndexesAsync();
            jsonSync.ValidateForeignKeys();

            await jsonSync.RecompactIfNeededAsync();
            await jsonSync.EncryptIfNeededAsync();

            // Phase F: Purge expired quarantined files.
            if (jsonOpts!.QuarantineMaxAgeDays > 0 && fsys is not null)
            {
                var purgeLogger = scope.ServiceProvider.GetRequiredService<ILogger<ColdEntityStore>>();
                QuarantineService.PurgeExpiredQuarantineFiles(
                    fsys, jsonOpts.DataDirectory, jsonOpts.QuarantineMaxAgeDays, purgeLogger, CancellationToken.None);
            }

            // Phase E: Start periodic cold index maintenance.
            var maintenance = services.GetService<ColdIndexMaintenanceService>();
            maintenance?.Start(jsonOpts!.IndexRescanIntervalMinutes);

            // Phase K: Start background flush worker when async flush is enabled.
            if (jsonOpts.AsyncFlush)
            {
                var flushWorker = services.GetService<FlushWorker>();
                flushWorker?.Start();
            }

            // Phase M: Create unclean shutdown sentinel.
            JsonPersistenceHealthCheck.CreateSentinel(fsys!, jsonOpts);
        }
    }

    /// <summary>
    /// Gracefully shuts down the persistence layer by acquiring all directory
    /// locks (draining in-flight I/O) and then disposing the lock manager.
    /// Call from <c>IHostApplicationLifetime.ApplicationStopping</c> or equivalent.
    /// </summary>
    public static async Task ShutdownInfrastructureAsync(this IServiceProvider services)
    {
        // Phase E: Stop periodic index maintenance first.
        var maintenance = services.GetService<ColdIndexMaintenanceService>();
        maintenance?.Dispose();

        // Phase K: Signal the flush queue to complete, then drain remaining items.
        var flushQueue = services.GetService<FlushQueue>();
        var flushWorker = services.GetService<FlushWorker>();
        if (flushQueue is not null)
        {
            flushQueue.Complete();
            if (flushWorker is not null)
            {
                try { await flushWorker.StopAsync(); }
                catch (OperationCanceledException) { }
            }
            flushQueue.Dispose();
        }
        flushWorker?.Dispose();

        // Phase M: Remove unclean shutdown sentinel on clean shutdown.
        var jsonOpts = services.GetService<JsonFileOptions>();
        var fsys = services.GetService<IPersistenceFileSystem>();
        if (jsonOpts is not null && fsys is not null)
            JsonPersistenceHealthCheck.RemoveSentinel(fsys, jsonOpts);

        // Dispose migration infrastructure.
        services.GetService<MigrationGate>()?.Dispose();
        (services.GetService<MigrationService>() as IDisposable)?.Dispose();

        var lockManager = services.GetService<DirectoryLockManager>();
        if (lockManager is not null)
        {
            await lockManager.AcquireAllAsync();
            lockManager.Dispose();
        }
    }
}
