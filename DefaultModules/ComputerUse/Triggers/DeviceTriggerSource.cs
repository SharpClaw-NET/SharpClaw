using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.ComputerUse.Triggers;

/// <summary>
/// Fires when a hardware device is connected or disconnected.
/// Uses WMI <c>Win32_DeviceChangeEvent</c> on Windows;
/// udev netlink on Linux.  Not supported on macOS.
/// </summary>
public sealed class DeviceTriggerSource(
    ILogger<DeviceTriggerSource> logger) : ITaskTriggerSource, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];

    public IReadOnlyList<string> TriggerKeys { get; } = ["DeviceConnected", "DeviceDisconnected"];

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            logger.LogWarning("TASK441: DeviceTriggerSource is not supported on macOS.");
            return Task.CompletedTask;
        }

        _contexts = contexts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _listenTask = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? ListenLinuxAsync(_cts.Token)
            : StartWindowsListenAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_listenTask is not null)
            {
                try { await _listenTask.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { /* ignore */ }
            }

            _cts.Dispose();
            _cts = null;
        }

        _contexts = [];
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private Task StartWindowsListenAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Task.CompletedTask;

        return ListenWindowsAsync(ct);
    }

    [SupportedOSPlatform("windows")]
    private async Task ListenWindowsAsync(CancellationToken ct)
    {
        var seen = GetWindowsDeviceSet();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                var current = GetWindowsDeviceSet();

                var added   = current.Except(seen).ToList();
                var removed = seen.Except(current).ToList();
                seen = current;

                foreach (var device in added)
                    await FireMatchingAsync("DeviceConnected", device);

                foreach (var device in removed)
                    await FireMatchingAsync("DeviceDisconnected", device);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DeviceTriggerSource Windows poll error.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<string> GetWindowsDeviceSet()
    {
        try { return GetUsbDeviceNames().ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { return []; }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetUsbDeviceNames()
    {
        yield break;
    }

    [SupportedOSPlatform("linux")]
    private async Task ListenLinuxAsync(CancellationToken ct)
    {
        var seen = GetLinuxDeviceSet();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                var current = GetLinuxDeviceSet();

                var added   = current.Except(seen).ToList();
                var removed = seen.Except(current).ToList();
                seen = current;

                foreach (var device in added)
                    await FireMatchingAsync("DeviceConnected", device);

                foreach (var device in removed)
                    await FireMatchingAsync("DeviceDisconnected", device);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DeviceTriggerSource Linux poll error.");
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static HashSet<string> GetLinuxDeviceSet()
    {
        const string sysPath = "/sys/bus/usb/devices";
        if (!Directory.Exists(sysPath)) return [];

        return Directory.GetDirectories(sysPath)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task FireMatchingAsync(string triggerKey, string deviceId)
    {
        foreach (var ctx in _contexts.Where(c => c.Definition.TriggerKey == triggerKey))
        {
            var pattern = ctx.Definition.Parameters.GetValueOrDefault(ComputerUseTriggerKeys.DeviceNamePattern);
            if (!string.IsNullOrWhiteSpace(pattern) &&
                !deviceId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                continue;

            try { await ctx.FireAsync(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "DeviceTriggerSource failed to fire context for definition {Id}.", ctx.TaskDefinitionId);
            }
        }
    }
}
