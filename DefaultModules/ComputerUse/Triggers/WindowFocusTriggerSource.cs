using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.ComputerUse.Triggers;

/// <summary>
/// Fires when a window gains or loses focus.
/// Uses <c>SetWinEventHook</c> on Windows; polls <c>_NET_ACTIVE_WINDOW</c> on Linux.
/// Not supported on macOS.
/// </summary>
public sealed class WindowFocusTriggerSource(
    ILogger<WindowFocusTriggerSource> logger) : ITaskTriggerSource, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    public IReadOnlyList<string> TriggerKeys { get; } = ["WindowFocused", "WindowBlurred"];

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            logger.LogWarning("TASK441: WindowFocusTriggerSource is not supported on macOS.");
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
        var lastFocused = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                var activeTitle = GetActiveWindowTitle();

                foreach (var ctx in _contexts)
                {
                    var processName = ctx.Definition.Parameters.GetValueOrDefault(ComputerUseTriggerKeys.ProcessName);
                    if (string.IsNullOrWhiteSpace(processName)) continue;

                    var isFocused = !string.IsNullOrEmpty(activeTitle) &&
                                    activeTitle.Contains(processName, StringComparison.OrdinalIgnoreCase);

                    var wasFocused = lastFocused.GetValueOrDefault(processName, false);
                    lastFocused[processName] = isFocused;

                    if (ctx.Definition.TriggerKey == ComputerUseTriggerKeys.WindowFocused && !wasFocused && isFocused)
                        await FireAsync(ctx);

                    if (ctx.Definition.TriggerKey == ComputerUseTriggerKeys.WindowBlurred && wasFocused && !isFocused)
                        await FireAsync(ctx);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WindowFocusTriggerSource poll error.");
            }
        }
    }

    private string? GetActiveWindowTitle()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetWindowsActiveTitle();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return GetLinuxActiveTitle();

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsActiveTitle()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero) return null;

        var len = GetWindowTextLength(hwnd);
        if (len == 0) return null;

        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [SupportedOSPlatform("linux")]
    private static string? GetLinuxActiveTitle()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("xdotool", "getactivewindow getwindowname")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            return proc?.StandardOutput.ReadToEnd().Trim();
        }
        catch { return null; }
    }

    private async Task FireAsync(ITaskTriggerSourceContext ctx)
    {
        try { await ctx.FireAsync(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "WindowFocusTriggerSource failed to fire context for definition {Id}.", ctx.TaskDefinitionId);
        }
    }

    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    private static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern int GetWindowTextLength(nint hWnd);
}
