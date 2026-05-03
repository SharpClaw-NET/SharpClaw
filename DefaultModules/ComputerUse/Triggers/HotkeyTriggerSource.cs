using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.ComputerUse.Triggers;

/// <summary>
/// Fires when a registered global hotkey combination is pressed.
/// Uses <c>RegisterHotKey</c> on Windows only.
/// Not supported on Linux or macOS.
/// </summary>
public sealed class HotkeyTriggerSource(
    ILogger<HotkeyTriggerSource> logger) : ITaskTriggerSource, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];
    private readonly List<int> _registeredIds = [];

    public string? TriggerKey => "Hotkey";

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logger.LogWarning("TASK441: HotkeyTriggerSource is only supported on Windows.");
            return Task.CompletedTask;
        }

        _contexts = contexts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pumpTask = StartWindowsHotkeysAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_pumpTask is not null)
            {
                try { await _pumpTask.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { /* ignore */ }
            }

            _cts.Dispose();
            _cts = null;
        }

        _registeredIds.Clear();
        _contexts = [];
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    [SupportedOSPlatform("windows")]
    private async Task StartWindowsHotkeysAsync(CancellationToken ct)
    {
        var id = 0;
        var contextMap = new Dictionary<int, ITaskTriggerSourceContext>();

        foreach (var ctx in _contexts)
        {
            var combo = ctx.Definition.Parameters.GetValueOrDefault(ComputerUseTriggerKeys.HotkeyCombo);
            if (string.IsNullOrWhiteSpace(combo)) continue;

            if (!TryParseHotkey(combo, out var modifiers, out var vk))
            {
                logger.LogWarning(
                    "HotkeyTriggerSource: could not parse hotkey '{Combo}' for definition {Id}.",
                    combo, ctx.TaskDefinitionId);
                continue;
            }

            if (RegisterHotKey(nint.Zero, id, modifiers, vk))
            {
                contextMap[id] = ctx;
                _registeredIds.Add(id);
                id++;
            }
            else
            {
                logger.LogWarning(
                    "HotkeyTriggerSource: failed to register hotkey '{Combo}' (Win32 error {Error}).",
                    combo, Marshal.GetLastWin32Error());
            }
        }

        if (contextMap.Count == 0) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (PeekMessage(out var msg, nint.Zero, 0x0312, 0x0312, 1 /* PM_REMOVE */))
                {
                    var hotKeyId = (int)(msg.wParam.ToInt64());
                    if (contextMap.TryGetValue(hotKeyId, out var ctx))
                        await FireAsync(ctx);
                }

                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        finally
        {
            foreach (var regId in _registeredIds)
                UnregisterHotKey(nint.Zero, regId);
        }
    }

    private static bool TryParseHotkey(string combo, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":  case "CONTROL": modifiers |= 0x0002; break;
                case "ALT":                   modifiers |= 0x0001; break;
                case "SHIFT":                 modifiers |= 0x0004; break;
                case "WIN":   case "WINDOWS": modifiers |= 0x0008; break;
                default:
                    if (part.Length == 1)
                        vk = (uint)char.ToUpperInvariant(part[0]);
                    else if (WellKnownVirtualKeys.TryGetValue(part.ToUpperInvariant(), out var wk))
                        vk = wk;
                    else
                        return false;
                    break;
            }
        }

        return vk != 0;
    }

    private async Task FireAsync(ITaskTriggerSourceContext ctx)
    {
        try { await ctx.FireAsync(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "HotkeyTriggerSource failed to fire context for definition {Id}.", ctx.TaskDefinitionId);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    private static readonly Dictionary<string, uint> WellKnownVirtualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F1"]  = 0x70, ["F2"]  = 0x71, ["F3"]  = 0x72, ["F4"]  = 0x73,
        ["F5"]  = 0x74, ["F6"]  = 0x75, ["F7"]  = 0x76, ["F8"]  = 0x77,
        ["F9"]  = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["DELETE"] = 0x2E, ["INSERT"] = 0x2D, ["HOME"] = 0x24, ["END"] = 0x23,
        ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22, ["SPACE"] = 0x20,
        ["RETURN"] = 0x0D, ["ENTER"] = 0x0D, ["ESCAPE"] = 0x1B, ["ESC"] = 0x1B,
        ["TAB"] = 0x09, ["BACK"] = 0x08, ["BACKSPACE"] = 0x08,
        ["UP"] = 0x26, ["DOWN"] = 0x28, ["LEFT"] = 0x25, ["RIGHT"] = 0x27,
    };
}
