using System.Collections.Concurrent;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using SharpClaw.Application.Services;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Background service that periodically probes enabled modules' health checks.
/// Modules that fail <c>HealthCheckFailureThreshold</c> consecutive checks are
/// automatically disabled. Results are cached for the REST endpoint.
/// </summary>
public sealed class ModuleHealthCheckService(
    ModuleRegistry registry,
    ModuleEventDispatcher eventDispatcher,
    IServiceProvider rootServices,
    IConfiguration configuration,
    ILogger<ModuleHealthCheckService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ModuleHealthStatus> _latestStatus = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failureCounts = new(StringComparer.Ordinal);

    /// <summary>Get the last known health status for a module.</summary>
    public ModuleHealthStatus? GetStatus(string moduleId) =>
        _latestStatus.TryGetValue(moduleId, out var s) ? s : null;

    /// <summary>Get all cached health statuses.</summary>
    public IReadOnlyDictionary<string, ModuleHealthStatus> GetAllStatuses() =>
        _latestStatus.ToDictionary(kv => kv.Key, kv => kv.Value);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("Modules:HealthCheckIntervalSeconds", 60);
        if (intervalSeconds <= 0)
        {
            logger.LogInformation("Module health checks disabled (interval = {Interval}s)", intervalSeconds);
            return;
        }

        var threshold = configuration.GetValue("Modules:HealthCheckFailureThreshold", 3);
        var timeoutSeconds = configuration.GetValue("Modules:HealthCheckTimeoutSeconds", 10);

        logger.LogInformation(
            "Module health check service started (interval={Interval}s, threshold={Threshold}, timeout={Timeout}s)",
            intervalSeconds, threshold, timeoutSeconds);

        // Initial delay to let modules finish initialization
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var module in registry.GetAllModules())
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                    var status = await module.HealthCheckAsync(cts.Token);
                    _latestStatus[module.Id] = status;

                    if (status.IsHealthy)
                    {
                        _failureCounts.TryRemove(module.Id, out _);
                    }
                    else
                    {
                        var count = _failureCounts.AddOrUpdate(module.Id, 1, (_, c) => c + 1);
                        logger.LogWarning(
                            "Module '{ModuleId}' health check failed ({Count}/{Threshold}): {Message}",
                            module.Id, count, threshold, status.Message);

                        if (count >= threshold)
                            await AutoDisableAsync(module.Id, threshold);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Health check timeout — treat as failure
                    var status = new ModuleHealthStatus(false, "Health check timed out");
                    _latestStatus[module.Id] = status;

                    var count = _failureCounts.AddOrUpdate(module.Id, 1, (_, c) => c + 1);
                    logger.LogWarning(
                        "Module '{ModuleId}' health check timed out ({Count}/{Threshold})",
                        module.Id, count, threshold);

                    if (count >= threshold)
                        await AutoDisableAsync(module.Id, threshold);
                }
                catch (Exception ex)
                {
                    var status = new ModuleHealthStatus(false, $"Health check threw: {ex.Message}");
                    _latestStatus[module.Id] = status;

                    var count = _failureCounts.AddOrUpdate(module.Id, 1, (_, c) => c + 1);
                    logger.LogWarning(ex,
                        "Module '{ModuleId}' health check threw ({Count}/{Threshold})",
                        module.Id, count, threshold);

                    if (count >= threshold)
                        await AutoDisableAsync(module.Id, threshold);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task AutoDisableAsync(string moduleId, int threshold)
    {
        logger.LogError(
            "Module '{ModuleId}' auto-disabled after {Threshold} consecutive health check failures",
            moduleId, threshold);

        _failureCounts.TryRemove(moduleId, out _);

        eventDispatcher.Dispatch(new SharpClawEvent(
            SharpClawEventType.ModuleHealthFailed,
            DateTimeOffset.UtcNow,
            SourceId: moduleId,
            Summary: $"Module '{moduleId}' auto-disabled after {threshold} consecutive health check failures"));

        try
        {
            using var scope = rootServices.CreateScope();
            var moduleSvc = scope.ServiceProvider.GetRequiredService<ModuleService>();
            await moduleSvc.DisableAsync(moduleId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to auto-disable module '{ModuleId}'", moduleId);
        }
    }
}
