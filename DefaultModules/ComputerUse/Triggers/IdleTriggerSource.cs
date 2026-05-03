using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.ComputerUse.Triggers;

/// <summary>
/// Fires when the system transitions to or from an idle state.
/// Uses <c>GetLastInputInfo</c> on Windows; polls the X11 screensaver idle
/// time on Linux.  Not supported on macOS.
/// </summary>
public sealed class IdleTriggerSource(
    ILogger<IdleTriggerSource> logger) : ITaskTriggerSource, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    public IReadOnlyList<string> TriggerKeys { get; } = ["SystemIdle", "SystemActive"];

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            logger.LogWarning("TASK441: IdleTriggerSource is not supported on macOS.");
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
        var wasIdle = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var idleSeconds = GetIdleSeconds();

                foreach (var ctx in _contexts)
                {
                    var minutes = TryParseInt(ctx.Definition.Parameters.GetValueOrDefault(ComputerUseTriggerKeys.IdleMinutes)) ?? 5;
                    var threshold = minutes * 60;
                    var isIdle    = idleSeconds >= threshold;

                    if (ctx.Definition.TriggerKey == ComputerUseTriggerKeys.SystemIdle && !wasIdle && isIdle)
                        await FireAsync(ctx);

                    if (ctx.Definition.TriggerKey == ComputerUseTriggerKeys.SystemActive && wasIdle && !isIdle)
                        await FireAsync(ctx);
                }

                wasIdle = _contexts.Any(c =>
                {
                    var m = TryParseInt(c.Definition.Parameters.GetValueOrDefault(ComputerUseTriggerKeys.IdleMinutes)) ?? 5;
                    return GetIdleSeconds() >= m * 60;
                });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "IdleTriggerSource poll error.");
            }
        }
    }

    private double GetIdleSeconds()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetWindowsIdleSeconds();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return GetLinuxIdleSeconds();

        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static double GetWindowsIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (Environment.TickCount64 - info.dwTime) / 1000.0;
    }

    [SupportedOSPlatform("linux")]
    private static double GetLinuxIdleSeconds()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("xprintidle")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return 0;
            var output = proc.StandardOutput.ReadToEnd();
            if (long.TryParse(output.Trim(), out var ms))
                return ms / 1000.0;
        }
        catch { /* xprintidle not available */ }

        return 0;
    }

    private async Task FireAsync(ITaskTriggerSourceContext ctx)
    {
        try { await ctx.FireAsync(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "IdleTriggerSource failed to fire context for definition {Id}.", ctx.TaskDefinitionId);
        }
    }

    private static int? TryParseInt(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
