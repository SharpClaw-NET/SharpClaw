using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.DTOs.Diagnostics;
using SharpClaw.Contracts.Enums;
using SharpClaw.Runtime.INF.DurableStorage;
using SharpClaw.Runtime.INF.Logging;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Shared.Logging;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class DurableStorageMaintenanceOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);
    public DurableRetentionOptions Logs { get; init; } = new();
    public long MaximumArtifactBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaximumArtifactDeletesPerRun { get; init; } = 10_000;
    public TimeSpan ArtifactOrphanGraceAge { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Applies age and disk-pressure policies to immutable segments and
/// unprotected artifacts. It never changes the selected EF provider and
/// keeps its latest outcome available to the health endpoint.
/// </summary>
public sealed class DurableStorageMaintenanceService(
    DurableSegmentStore records,
    ExecutionArtifactStore artifacts,
    IServiceScopeFactory scopes,
    SharpClawLogRuntime logging,
    DurableStorageMaintenanceOptions options,
    ILogger<DurableStorageMaintenanceService> logger) : BackgroundService
{
    private readonly object _stateGate = new();
    private DurableRetentionResult? _lastLogResult;
    private ArtifactRetentionResult? _lastArtifactResult;
    private DateTimeOffset? _lastRun;
    private string? _failure;

    public static void ValidateOptions(DurableStorageMaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Interval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "Durable storage maintenance interval must be positive.");
        if (options.MaximumArtifactBytes < 0
            || options.MinimumFreeBytes < 0
            || options.MaximumArtifactDeletesPerRun < 1
            || options.ArtifactOrphanGraceAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Durable storage artifact retention limits are invalid.");
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var databaseProtection = await ReadDatabaseProtectionAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var logResult = await records.ApplyRetentionAsync(
                    options.Logs,
                    cancellationToken)
                .ConfigureAwait(false);
            var streamArtifacts = await records.ReadArtifactReferencesAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            databaseProtection.ArtifactIds.UnionWith(streamArtifacts);
            var artifactResult = await artifacts.ApplyRetentionAsync(
                    databaseProtection.ArtifactIds,
                    options.Logs.JobLogAge,
                    options.ArtifactOrphanGraceAge,
                    options.MaximumArtifactBytes,
                    options.MinimumFreeBytes,
                    options.MaximumArtifactDeletesPerRun,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_stateGate)
            {
                _lastLogResult = logResult;
                _lastArtifactResult = artifactResult;
                _lastRun = DateTimeOffset.UtcNow;
                _failure = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_stateGate)
            {
                _lastRun = DateTimeOffset.UtcNow;
                _failure = ex.Message;
            }
            logger.LogError(ex, "Durable storage maintenance failed.");
        }
    }

    public DurableStorageHealthResponse GetHealth()
    {
        var logSnapshot = records.GetSnapshot();
        var artifactSnapshot = artifacts.GetSnapshot();
        lock (_stateGate)
        {
            var quotaSatisfied = (_lastLogResult?.QuotaSatisfied ?? true)
                && (_lastArtifactResult?.QuotaSatisfied ?? true);
            var reason = _failure
                ?? logSnapshot.DegradedReason
                ?? (!quotaSatisfied
                    ? "Durable storage quota or free-space reserve is not satisfied."
                    : null);
            return new DurableStorageHealthResponse(
                reason is null,
                reason,
                logSnapshot.EncodedBytes,
                artifactSnapshot.EncodedBytes,
                logSnapshot.ActiveStreams,
                logSnapshot.SealedSegments,
                logging.Dispatcher.QueueDepth,
                logSnapshot.LastSuccessfulFlush,
                _lastRun);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ValidateOptions(options);
        await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task<DatabaseProtection> ReadDatabaseProtectionAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var artifactIds = await db.AgentJobs
            .Select(job => EF.Property<Guid?>(
                job,
                ExecutionMetadataColumns.ResultArtifactId))
            .Where(id => id != null)
            .ToListAsync(cancellationToken);
        return new DatabaseProtection(
            artifactIds.Select(id => id!.Value).ToHashSet());
    }

    private sealed record DatabaseProtection(
        HashSet<Guid> ArtifactIds);
}
