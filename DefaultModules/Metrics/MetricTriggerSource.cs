using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Metrics;

/// <summary>
/// Fires when the value of a named metric crosses a configured threshold.
/// Polls all registered <see cref="ITaskMetricProvider"/> implementations on a timer.
/// </summary>
public sealed class MetricTriggerSource(
    IEnumerable<ITaskMetricProvider> metricProviders,
    ILogger<MetricTriggerSource> logger) : ITaskTriggerSource, IAsyncDisposable
{
    private readonly IReadOnlyList<ITaskMetricProvider> _providers = metricProviders.ToList().AsReadOnly();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    public string TriggerKey => MetricTriggerKeys.MetricThreshold;

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        _contexts = contexts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = PollAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_pollTask is not null)
            {
                try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { /* ignore */ }
            }

            _cts.Dispose();
            _cts = null;
        }

        _contexts = [];
    }

    public string? GetBindingValue(TaskTriggerDefinition t) => t.Parameters.GetValueOrDefault(MetricTriggerKeys.Source);

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task PollAsync(CancellationToken ct)
    {
        // Track whether each context's threshold was crossed last poll
        var crossedLast = new Dictionary<Guid, bool>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                foreach (var ctx in _contexts)
                {
                    var def = ctx.Definition;
                    var source = def.Parameters.GetValueOrDefault(MetricTriggerKeys.Source);
                    if (string.IsNullOrWhiteSpace(source)) continue;

                    var provider = _providers.FirstOrDefault(p =>
                        string.Equals(p.MetricName, source, StringComparison.OrdinalIgnoreCase));

                    if (provider is null) continue;

                    double value;
                    try { value = await provider.GetValueAsync(ct); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "MetricTriggerSource: provider '{Name}' threw.", provider.MetricName);
                        continue;
                    }

                    var threshold = TryParseDouble(def.Parameters.GetValueOrDefault(MetricTriggerKeys.Threshold)) ?? 0;
                    var direction = ParseDirection(def.Parameters.GetValueOrDefault(MetricTriggerKeys.Direction));
                    var crossed = direction switch
                    {
                        ThresholdDirection.Above  => value > threshold,
                        ThresholdDirection.Below  => value < threshold,
                        ThresholdDirection.Either => value > threshold || value < threshold,
                        _                          => false,
                    };

                    var wasCrossed = crossedLast.GetValueOrDefault(ctx.TaskDefinitionId, false);
                    crossedLast[ctx.TaskDefinitionId] = crossed;

                    if (crossed && !wasCrossed)
                        await FireAsync(ctx);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MetricTriggerSource poll error.");
            }
        }
    }

    private async Task FireAsync(ITaskTriggerSourceContext ctx)
    {
        try { await ctx.FireAsync(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "MetricTriggerSource failed to fire context for definition {Id}.", ctx.TaskDefinitionId);
        }
    }

    private static double? TryParseDouble(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    private static ThresholdDirection ParseDirection(string? value) =>
        Enum.TryParse<ThresholdDirection>(value, ignoreCase: true, out var parsed)
            ? parsed
            : default;
}
