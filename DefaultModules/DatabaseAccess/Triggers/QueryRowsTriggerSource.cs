using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.DatabaseAccess.Services;

namespace SharpClaw.Modules.DatabaseAccess.Triggers;

/// <summary>
/// Fires when a raw SQL query returns one or more rows, polled on a timer.
/// </summary>
public sealed class QueryRowsTriggerSource(
    IDatabaseQueryExecutor queryExecutor,
    ILogger<QueryRowsTriggerSource> logger) : ITaskTriggerSource, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    public string? TriggerKey => "QueryReturnsRows";

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

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var minInterval = _contexts
                    .Select(c => TryParseInt(c.Definition.Parameters.GetValueOrDefault(DatabaseAccessTriggerKeys.QueryPollIntervalSecs)) ?? 60)
                    .DefaultIfEmpty(60)
                    .Min();

                await Task.Delay(TimeSpan.FromSeconds(minInterval), ct);

                foreach (var ctx in _contexts)
                {
                    var sql = ctx.Definition.Parameters.GetValueOrDefault(DatabaseAccessTriggerKeys.SqlQuery);
                    if (string.IsNullOrWhiteSpace(sql)) continue;

                    try
                    {
                        if (await queryExecutor.HasRowsAsync(sql, ct))
                            await FireAsync(ctx);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "QueryRowsTriggerSource: query failed for definition {Id}.", ctx.TaskDefinitionId);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "QueryRowsTriggerSource poll error.");
            }
        }
    }

    private async Task FireAsync(ITaskTriggerSourceContext ctx)
    {
        try { await ctx.FireAsync(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "QueryRowsTriggerSource failed to fire context for definition {Id}.", ctx.TaskDefinitionId);
        }
    }

    private static int? TryParseInt(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
}
