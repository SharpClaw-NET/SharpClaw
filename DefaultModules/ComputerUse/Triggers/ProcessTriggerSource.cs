using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.ComputerUse.Triggers;

/// <summary>
/// Fires when a monitored process starts or stops.
/// Uses WMI event queries on Windows; polls <c>/proc</c> on Linux.
/// Not supported on macOS.
/// </summary>
public sealed class ProcessTriggerSource(
    ILogger<ProcessTriggerSource> logger) : ITaskTriggerSource, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    public IReadOnlyList<string> TriggerKeys { get; } = ["ProcessStarted", "ProcessStopped"];

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            logger.LogWarning("TASK441: ProcessTriggerSource is not supported on macOS.");
            return Task.CompletedTask;
        }

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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);

                var runningSet = GetRunningProcessNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var ctx in _contexts)
                {
                    var name = ctx.Definition.Parameters.GetValueOrDefault(ComputerUseTriggerKeys.ProcessName);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var wasRunning = seen.Contains(name);
                    var isRunning  = runningSet.Contains(name);

                    if (ctx.Definition.TriggerKey == ComputerUseTriggerKeys.ProcessStarted && !wasRunning && isRunning)
                        await FireAsync(ctx);

                    if (ctx.Definition.TriggerKey == ComputerUseTriggerKeys.ProcessStopped && wasRunning && !isRunning)
                        await FireAsync(ctx);
                }

                seen = runningSet;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ProcessTriggerSource poll error.");
            }
        }
    }

    private static IEnumerable<string> GetRunningProcessNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return GetLinuxProcessNames();

        return System.Diagnostics.Process.GetProcesses()
            .Select(p => p.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("linux")]
    private static IEnumerable<string> GetLinuxProcessNames()
    {
        if (!Directory.Exists("/proc")) return [];

        return Directory.EnumerateDirectories("/proc")
            .Where(d => Path.GetFileName(d).All(char.IsDigit))
            .Select(d =>
            {
                try { return File.ReadAllText(Path.Combine(d, "comm")).Trim(); }
                catch { return null; }
            })
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task FireAsync(ITaskTriggerSourceContext ctx)
    {
        try { await ctx.FireAsync(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ProcessTriggerSource failed to fire context for definition {Id}.", ctx.TaskDefinitionId);
        }
    }
}
