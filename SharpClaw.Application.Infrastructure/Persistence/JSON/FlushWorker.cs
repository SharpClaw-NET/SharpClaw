using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Infrastructure.Persistence.JSON;

/// <summary>
/// Background worker that dequeues <see cref="FlushQueue.FlushIntent"/> items
/// and calls <see cref="JsonFilePersistenceService.FlushChangesAsync"/> to
/// persist them to disk via the two-phase commit pipeline.
/// <para>
/// <b>Phase K:</b> Single consumer — the <see cref="FlushQueue"/> channel is
/// configured with <c>SingleReader = true</c>.
/// </para>
/// </summary>
public sealed class FlushWorker : IDisposable
{
    private readonly FlushQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<FlushWorker> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    /// <summary>Maximum consecutive retry attempts before dropping a flush intent.</summary>
    private const int MaxRetries = 3;

    /// <summary>Base delay between retries (exponential backoff).</summary>
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(200);

    public FlushWorker(
        FlushQueue queue,
        IServiceProvider services,
        ILogger<FlushWorker> logger)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Starts the background consumer loop.
    /// </summary>
    public void Start()
    {
        _runTask = Task.Run(() => ExecuteAsync(_cts.Token));
        _logger.LogInformation("FlushWorker started");
    }

    /// <summary>
    /// Stops the worker and drains remaining items.
    /// </summary>
    internal async Task StopAsync()
    {
        _queue.Complete();
        if (_runTask is not null)
        {
            try { await _runTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                FlushQueue.FlushIntent intent;
                try
                {
                    intent = await _queue.DequeueAsync(stoppingToken);
                }
                catch (ChannelClosedException)
                {
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                await ProcessIntentAsync(intent, stoppingToken);

                // If queue is empty and a drain was requested, signal completion.
                if (_queue.Count == 0)
                    _queue.CompleteDrain();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        // Drain remaining items on shutdown.
        await DrainRemainingAsync();

        _logger.LogInformation("FlushWorker stopped");
    }

    private async Task ProcessIntentAsync(FlushQueue.FlushIntent intent, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var scope = _services.CreateScope();
                var persistence = scope.ServiceProvider
                    .GetRequiredService<JsonFilePersistenceService>();

                await persistence.FlushChangesAsync(
                    intent.EntityChanges, intent.JoinTableChanges,
                    intent.SerializedEntitiesByClrType, ct);

                _queue.RemoveOverlayEntries(intent);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Don't retry on shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FlushWorker attempt {Attempt}/{MaxRetries} failed for intent ({EntityCount} entities)",
                    attempt, MaxRetries, intent.EntityChanges.Count);

                if (attempt < MaxRetries)
                {
                    var delay = RetryBaseDelay * (1 << (attempt - 1));
                    await Task.Delay(delay, ct);
                }
                else
                {
                    _logger.LogError(ex,
                        "FlushWorker exhausted retries for intent ({EntityCount} entities). " +
                        "Data remains in overlay until next successful flush or shutdown.",
                        intent.EntityChanges.Count);
                }
            }
        }
    }

    /// <summary>
    /// Drains any items remaining in the channel after the stopping token fires.
    /// </summary>
    private async Task DrainRemainingAsync()
    {
        while (_queue.TryRead(out var intent) && intent is not null)
        {
            try
            {
                using var scope = _services.CreateScope();
                var persistence = scope.ServiceProvider
                    .GetRequiredService<JsonFilePersistenceService>();

                await persistence.FlushChangesAsync(
                    intent.EntityChanges, intent.JoinTableChanges,
                    intent.SerializedEntitiesByClrType, CancellationToken.None);

                _queue.RemoveOverlayEntries(intent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FlushWorker failed to drain intent on shutdown");
            }
        }

        _queue.Overlay.Clear();
        _queue.CompleteDrain();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
