using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Core.Services.Triggers;

/// <summary>
/// Background service that loads active <see cref="TaskTriggerBindingDB"/> rows at
/// startup, starts the appropriate <see cref="ITaskTriggerSource"/> implementations,
/// and reloads sources when bindings change.
/// </summary>
public sealed class TaskTriggerHostService(
    IServiceProvider services,
    IEnumerable<ITaskTriggerSource> sources,
    ILogger<TaskTriggerHostService> logger) : BackgroundService
{
    private readonly IReadOnlyList<ITaskTriggerSource> _sources = sources.ToList().AsReadOnly();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private CancellationToken _stoppingToken;

    // ──────────────────────────────────────────────────────────────
    // BackgroundService
    // ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        await LoadAndStartSourcesAsync(stoppingToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping all trigger sources.");

        var stops = _sources.Select(async s =>
        {
            try { await s.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Trigger source {SourceType} did not stop cleanly.", s.GetType().Name);
            }
        });

        await Task.WhenAll(stops);
        await base.StopAsync(cancellationToken);
    }

    // ──────────────────────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="TaskTriggerRegistrar"/> after binding rows are
    /// changed.  Reloads all sources so they pick up the new binding set.
    /// </summary>
    public async Task NotifyBindingsChangedAsync()
    {
        await _reloadLock.WaitAsync();
        try
        {
            await StopAllSourcesAsync();
            await LoadAndStartSourcesAsync(_stoppingToken);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>
    /// Reloads a specific custom source by name. Used when a module is toggled
    /// or when bindings for a specific custom source are modified.
    /// </summary>
    /// <param name="sourceName">The custom source name to reload.</param>
    public async Task ReloadSourceAsync(string sourceName)
    {
        await _reloadLock.WaitAsync();
        try
        {
            // Find the source by its TriggerKey
            var source = _sources.FirstOrDefault(s =>
                s.TriggerKeys.Any(k => string.Equals(k, sourceName, StringComparison.OrdinalIgnoreCase)));

            if (source is null)
            {
                logger.LogWarning(
                    "Cannot reload trigger source '{SourceName}': no registered source with that key.",
                    sourceName);
                return;
            }

            // Stop the source
            try
            {
                await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Custom source '{SourceName}' did not stop cleanly during reload.", sourceName);
            }

            // Reload bindings for this source
            List<TaskTriggerBindingDB> bindings;
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
                bindings = await db.TaskTriggerBindings
                    .Where(b => b.IsEnabled && b.Kind == sourceName)
                    .ToListAsync(_stoppingToken);
            }

            if (bindings.Count == 0)
            {
                logger.LogInformation(
                    "Custom source '{SourceName}' has no enabled bindings; not starting.", 
                    sourceName);
                return;
            }

            var contexts = bindings
                .Select(b => BuildContext(b))
                .Where(c => c is not null)
                .Cast<ITaskTriggerSourceContext>()
                .ToList();

            if (contexts.Count == 0) return;

            try
            {
                await source.StartAsync(contexts, _stoppingToken);
                logger.LogInformation(
                    "Reloaded custom source '{SourceName}' with {Count} binding(s).",
                    sourceName, contexts.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Custom source '{SourceName}' failed to restart.", sourceName);
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Internal helpers
    // ──────────────────────────────────────────────────────────────

    private async Task LoadAndStartSourcesAsync(CancellationToken ct)
    {
        List<TaskTriggerBindingDB> bindings;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            bindings = await db.TaskTriggerBindings
                .Where(b => b.IsEnabled)
                .ToListAsync(ct);
        }

        logger.LogInformation(
            "TaskTriggerHostService: loaded {Count} enabled binding(s).", bindings.Count);

        // Group bindings by source
        foreach (var source in _sources)
        {
            var matching = bindings
                .Where(b => IsSourceMatch(source, b))
                .ToList();

            if (matching.Count == 0) continue;

            var contexts = matching
                .Select(b => BuildContext(b))
                .Where(c => c is not null)
                .Cast<ITaskTriggerSourceContext>()
                .ToList();

            if (contexts.Count == 0) continue;

            try
            {
                await source.StartAsync(contexts, ct);
                logger.LogDebug(
                    "Started trigger source {SourceType} with {Count} binding(s).",
                    source.GetType().Name, contexts.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Trigger source {SourceType} failed to start.", source.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Determines whether a binding row belongs to the given source by matching
    /// the binding's <c>Kind</c> column against the source's <see cref="ITaskTriggerSource.TriggerKeys"/>.
    /// </summary>
    private static bool IsSourceMatch(ITaskTriggerSource source, TaskTriggerBindingDB binding)
    {
        var keys = source.TriggerKeys;
        return keys.Any(k => string.Equals(k, binding.Kind, StringComparison.OrdinalIgnoreCase));
    }

    private async Task StopAllSourcesAsync()
    {
        foreach (var source in _sources)
        {
            try { await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Trigger source {SourceType} did not stop cleanly during reload.", source.GetType().Name);
            }
        }
    }

    private ITaskTriggerSourceContext? BuildContext(TaskTriggerBindingDB binding)
    {
        TaskTriggerDefinition? definition;
        try
        {
            definition = System.Text.Json.JsonSerializer.Deserialize<TaskTriggerDefinition>(
                binding.DefinitionJson);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not deserialize DefinitionJson for binding {BindingId}; skipping.",
                binding.Id);
            return null;
        }

        if (definition is null) return null;

        return new TriggerSourceContext(
            definition,
            binding.TaskDefinitionId,
            services,
            logger);
    }

    // ──────────────────────────────────────────────────────────────
    // Context implementation
    // ──────────────────────────────────────────────────────────────

    private sealed class TriggerSourceContext(
        TaskTriggerDefinition definition,
        Guid taskDefinitionId,
        IServiceProvider services,
        ILogger logger) : ITaskTriggerSourceContext
    {
        public TaskTriggerDefinition Definition { get; } = definition;
        public Guid TaskDefinitionId { get; } = taskDefinitionId;

        public async Task FireAsync(
            IReadOnlyDictionary<string, string>? parameters = null,
            CancellationToken ct = default)
        {
            using var scope = services.CreateScope();
            var taskService = scope.ServiceProvider.GetRequiredService<TaskService>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<TaskOrchestrator>();

            var request = new StartTaskInstanceRequest(
                TaskDefinitionId: TaskDefinitionId,
                ParameterValues:  parameters?.ToDictionary(kv => kv.Key, kv => kv.Value));

            TaskInstanceResponse instance;
            try
            {
                instance = await taskService.CreateInstanceAsync(request, ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Trigger could not create instance for definition {DefinitionId}.", TaskDefinitionId);
                return;
            }

            try
            {
                await orchestrator.StartAsync(instance.Id, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Trigger could not start instance {InstanceId}.", instance.Id);
            }
        }
    }
}
