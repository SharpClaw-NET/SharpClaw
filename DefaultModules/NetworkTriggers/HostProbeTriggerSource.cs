using System.Net.NetworkInformation;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.NetworkTriggers;

/// <summary>
/// Fires when a monitored host becomes reachable or unreachable, checked via
/// TCP connect or ICMP ping on a timer.
/// <para>
/// Moved out of <c>SharpClaw.Application.Core</c> by the trigger-extraction
/// plan; behavior is preserved verbatim.
/// </para>
/// </summary>
public sealed class HostProbeTriggerSource(
    ILogger<HostProbeTriggerSource> logger) : ITaskTriggerSource, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _probeTask;
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    public IReadOnlyList<string> TriggerKeys { get; } =
        [NetworkTriggerKeys.HostReachable, NetworkTriggerKeys.HostUnreachable];

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        _contexts = contexts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _probeTask = ProbeLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_probeTask is not null)
            {
                try { await _probeTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* ignore */ }
            }

            _cts.Dispose();
            _cts = null;
        }

        _contexts = [];
    }

    /// <inheritdoc />
    public string? GetBindingValue(TaskTriggerDefinition def) => def.TriggerKey switch
    {
        NetworkTriggerKeys.HostReachable or NetworkTriggerKeys.HostUnreachable =>
            def.Parameters.GetValueOrDefault(NetworkTriggerKeys.HostName),
        _ => null,
    };

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        // Track last known reachability per host:port key
        var lastState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                foreach (var ctx in _contexts)
                {
                    var host = ctx.Definition.Parameters.GetValueOrDefault(NetworkTriggerKeys.HostName);
                    if (string.IsNullOrWhiteSpace(host)) continue;

                    var port    = TryParseInt(ctx.Definition.Parameters.GetValueOrDefault(NetworkTriggerKeys.HostPort));
                    var key     = port.HasValue ? $"{host}:{port}" : host;
                    var reachable = await IsReachableAsync(host, port, ct);
                    var wasReachable = lastState.GetValueOrDefault(key, !reachable);

                    lastState[key] = reachable;

                    if (ctx.Definition.TriggerKey == NetworkTriggerKeys.HostReachable && !wasReachable && reachable)
                        await FireAsync(ctx);

                    if (ctx.Definition.TriggerKey == NetworkTriggerKeys.HostUnreachable && wasReachable && !reachable)
                        await FireAsync(ctx);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "HostProbeTriggerSource probe error.");
            }
        }
    }

    private static async Task<bool> IsReachableAsync(string host, int? port, CancellationToken ct)
    {
        if (port.HasValue)
        {
            try
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(host, port.Value, cts.Token);
                return true;
            }
            catch { return false; }
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 3000);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private async Task FireAsync(ITaskTriggerSourceContext ctx)
    {
        try { await ctx.FireAsync(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "HostProbeTriggerSource failed to fire context for definition {Id}.", ctx.TaskDefinitionId);
        }
    }

    private static int? TryParseInt(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
}
