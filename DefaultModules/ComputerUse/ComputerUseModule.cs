using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.DisplayDevices;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Modules.Contracts;

namespace SharpClaw.Modules.ComputerUse;

/// <summary>
/// Default module: desktop awareness, window management, input simulation,
/// clipboard access, and process control. Windows only.
/// </summary>
public sealed class ComputerUseModule : ISharpClawModule
{
    public string Id => "sharpclaw_computer_use";
    public string DisplayName => "Computer Use";
    public string ToolPrefix => "cu";

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<DesktopAwarenessService>();
        services.AddSingleton<DisplayCaptureService>();
        services.AddSingleton<DesktopInputService>();
        services.AddSingleton<IWindowManager>(sp =>
            sp.GetRequiredService<DesktopAwarenessService>());
        services.AddSingleton<IDesktopInput>(sp =>
            sp.GetRequiredService<DesktopInputService>());
    }

    // ═══════════════════════════════════════════════════════════════
    // Contracts
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractExport> ExportedContracts =>
    [
        new("window_management", typeof(IWindowManager),
            "Window enumeration, focus, capture, close"),
        new("desktop_input", typeof(IDesktopInput),
            "Mouse click, keyboard input, hotkey simulation"),
    ];

    // ═══════════════════════════════════════════════════════════════
    // CLI Commands
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "cu",
            Aliases: ["computer-use"],
            Scope: ModuleCliScope.TopLevel,
            Description: "Computer Use module commands",
            UsageLines:
            [
                "cu windows                     List visible desktop windows",
                "cu displays                    List registered display devices",
                "cu apps                        List registered native applications",
            ],
            Handler: HandleCuCommandAsync),
    ];

    private static readonly JsonSerializerOptions CliJsonPrint = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static async Task HandleCuCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            PrintCuUsage();
            return;
        }

        var sub = args[1].ToLowerInvariant();
        switch (sub)
        {
            case "windows":
            {
                var desktop = sp.GetRequiredService<DesktopAwarenessService>();
                Console.WriteLine(desktop.EnumerateWindows());
                break;
            }
            case "displays":
            {
                var svc = sp.GetRequiredService<DisplayDeviceService>();
                var list = await svc.ListAsync(ct);
                Console.WriteLine(JsonSerializer.Serialize(list, CliJsonPrint));
                break;
            }
            case "apps":
            {
                var svc = sp.GetRequiredService<NativeApplicationService>();
                var list = await svc.ListAsync(ct);
                Console.WriteLine(JsonSerializer.Serialize(list, CliJsonPrint));
                break;
            }
            default:
                Console.Error.WriteLine($"Unknown cu command: {sub}");
                PrintCuUsage();
                break;
        }
    }

    private static void PrintCuUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  cu windows    List visible desktop windows");
        Console.WriteLine("  cu displays   List registered display devices");
        Console.WriteLine("  cu apps       List registered native applications");
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var global = new ModuleToolPermission(
            IsPerResource: false, Check: null, DelegateTo: "EnumerateWindowsAsync");
        var display = new ModuleToolPermission(
            IsPerResource: true, Check: null, DelegateTo: "AccessDisplayDeviceAsync");
        var click = new ModuleToolPermission(
            IsPerResource: true, Check: null, DelegateTo: "ClickDesktopAsync");
        var type = new ModuleToolPermission(
            IsPerResource: true, Check: null, DelegateTo: "TypeOnDesktopAsync");
        var app = new ModuleToolPermission(
            IsPerResource: true, Check: null, DelegateTo: "LaunchNativeApplicationAsync");

        return
        [
            new("capture_display",
                "Screenshot a display; base64 PNG (vision) or text fallback.",
                ResourceOnlySchema(), display),
            new("click_desktop",
                "Click (x,y) on display. Returns screenshot.",
                ClickDesktopSchema(), click),
            new("type_on_desktop",
                "Type text; optional (x,y) to focus first. Returns screenshot.",
                TypeOnDesktopSchema(), type),
            new("enumerate_windows",
                "List visible desktop windows across all displays. Returns JSON array with title, processName, processId, executablePath. Windows only.",
                GlobalSchema(), global),
            new("launch_application",
                "Start a registered native application. Optionally open a file with it. Returns PID and window title.",
                LaunchApplicationSchema(), app),
            new("focus_window",
                "Bring window to foreground by PID, process name, or title substring. Windows only.",
                WindowTargetSchema(), global),
            new("close_window",
                "Send graceful close (WM_CLOSE) to a window. App may prompt to save. Windows only.",
                WindowTargetSchema(), global),
            new("resize_window",
                "Move/resize/minimize/maximize a window. Windows only.",
                ResizeWindowSchema(), global),
            new("send_hotkey",
                "Send keyboard shortcut (e.g. 'ctrl+s', 'alt+tab'). Optional focus-first by PID/title. Windows only.",
                SendHotkeySchema(), global),
            new("capture_window",
                "Screenshot a single window by PID/title. Smaller than capture_display. Returns base64 PNG (vision) or dims.",
                WindowTargetSchema(), global),
            new("read_clipboard",
                "Read clipboard: text, file list, or image. Auto-detect or specify format.",
                ReadClipboardSchema(), global),
            new("write_clipboard",
                "Set clipboard to text or file paths. Pair with send_hotkey('ctrl+v') for paste.",
                WriteClipboardSchema(), global),
            new("stop_process",
                "Stop a process launched via launch_application. Must match a registered native app.",
                StopProcessSchema(), app),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
    {
        var desktop = sp.GetRequiredService<DesktopAwarenessService>();
        var capture = sp.GetRequiredService<DisplayCaptureService>();
        var input = sp.GetRequiredService<DesktopInputService>();

        return toolName switch
        {
            "capture_display" => await CaptureDisplayAsync(job, capture, sp, ct),
            "click_desktop" => await ClickDesktopAsync(parameters, job, capture, input, sp, ct),
            "type_on_desktop" => await TypeOnDesktopAsync(parameters, job, capture, input, sp, ct),
            "enumerate_windows" => desktop.EnumerateWindows(),
            "launch_application" => await LaunchApplicationAsync(parameters, job, desktop, sp, ct),
            "focus_window" => desktop.FocusWindow(
                Int(parameters, "processId"), Str(parameters, "processName"), Str(parameters, "titleContains")),
            "close_window" => desktop.CloseWindow(
                Int(parameters, "processId"), Str(parameters, "processName"), Str(parameters, "titleContains")),
            "resize_window" => desktop.ResizeWindow(
                Int(parameters, "processId"), Str(parameters, "titleContains"),
                Int(parameters, "x"), Int(parameters, "y"),
                Int(parameters, "width"), Int(parameters, "height"),
                Str(parameters, "state")),
            "send_hotkey" => desktop.SendHotkey(
                Str(parameters, "keys")
                    ?? throw new InvalidOperationException("send_hotkey requires 'keys'."),
                Int(parameters, "processId"), Str(parameters, "titleContains")),
            "capture_window" => desktop.CaptureWindow(
                Int(parameters, "processId"), Str(parameters, "processName"), Str(parameters, "titleContains")),
            "read_clipboard" => await desktop.ReadClipboardAsync(Str(parameters, "format")),
            "write_clipboard" => await WriteClipboardAsync(parameters, desktop),
            "stop_process" => await StopProcessAsync(parameters, job, desktop, sp, ct),
            _ => throw new InvalidOperationException($"Unknown Computer Use tool: {toolName}"),
        };
    }

    // ── Display capture ───────────────────────────────────────────

    private static async Task<string> CaptureDisplayAsync(
        AgentJobContext job, DisplayCaptureService capture,
        IServiceProvider sp, CancellationToken ct)
    {
        var device = await ResolveDisplayAsync(job.ResourceId, sp, ct);

        byte[] imageBytes = OperatingSystem.IsWindows()
            ? capture.CaptureWindowsDisplay(device.DisplayIndex)
            : throw new PlatformNotSupportedException(
                "Display capture is only supported on Windows.");

        return $"Screenshot captured ({imageBytes.Length} bytes) of display '{device.Name}'\n[SCREENSHOT_BASE64]{Convert.ToBase64String(imageBytes)}";
    }

    // ── Click desktop ─────────────────────────────────────────────

    private static async Task<string> ClickDesktopAsync(
        JsonElement parameters, AgentJobContext job,
        DisplayCaptureService capture, DesktopInputService input,
        IServiceProvider sp, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Desktop click is only supported on Windows.");

        var device = await ResolveDisplayAsync(job.ResourceId, sp, ct);

        var x = Int(parameters, "x")
            ?? throw new InvalidOperationException("click_desktop requires 'x'.");
        var y = Int(parameters, "y")
            ?? throw new InvalidOperationException("click_desktop requires 'y'.");
        var button = Str(parameters, "button") ?? "left";
        var clickType = Str(parameters, "clickType") ?? "single";

        var bounds = capture.GetDisplayBounds(device.DisplayIndex);
        var absX = bounds.X + x;
        var absY = bounds.Y + y;

        input.PerformClick(absX, absY, button, clickType);

        var imageBytes = capture.CaptureWindowsDisplayWithClickMarker(
            device.DisplayIndex, x, y);
        return $"Clicked {button} ({clickType}) at ({x},{y}) on '{device.Name}'\n[SCREENSHOT_BASE64]{Convert.ToBase64String(imageBytes)}";
    }

    // ── Type on desktop ───────────────────────────────────────────

    private static async Task<string> TypeOnDesktopAsync(
        JsonElement parameters, AgentJobContext job,
        DisplayCaptureService capture, DesktopInputService input,
        IServiceProvider sp, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Desktop typing is only supported on Windows.");

        var device = await ResolveDisplayAsync(job.ResourceId, sp, ct);
        var text = Str(parameters, "text")
            ?? throw new InvalidOperationException("type_on_desktop requires 'text'.");

        var clickX = Int(parameters, "x");
        var clickY = Int(parameters, "y");
        if (clickX.HasValue && clickY.HasValue)
        {
            var bounds = capture.GetDisplayBounds(device.DisplayIndex);
            input.PerformClick(
                bounds.X + clickX.Value, bounds.Y + clickY.Value, "left", "single");
            await Task.Delay(100, ct);
        }

        input.PerformType(text);

        await Task.Delay(200, ct);
        var imageBytes = capture.CaptureWindowsDisplay(device.DisplayIndex);
        return $"Typed {text.Length} characters on '{device.Name}'\n[SCREENSHOT_BASE64]{Convert.ToBase64String(imageBytes)}";
    }

    // ── Launch application ────────────────────────────────────────

    private static async Task<string> LaunchApplicationAsync(
        JsonElement parameters, AgentJobContext job,
        DesktopAwarenessService desktop, IServiceProvider sp,
        CancellationToken ct)
    {
        var nativeAppService = sp.GetRequiredService<NativeApplicationService>();

        NativeApplicationDB? app = null;
        if (job.ResourceId.HasValue)
            app = await nativeAppService.ResolveAsync(
                job.ResourceId.Value.ToString(), ct);

        if (app is null)
        {
            var alias = Str(parameters, "alias");
            if (!string.IsNullOrWhiteSpace(alias))
                app = await nativeAppService.ResolveAsync(alias, ct);
        }

        if (app is null)
            throw new InvalidOperationException(
                job.ResourceId.HasValue
                    ? $"Native application {job.ResourceId} not found."
                    : "launch_application requires a targetId or alias.");

        return await desktop.LaunchApplicationAsync(
            app, Str(parameters, "arguments"), Str(parameters, "filePath"), ct);
    }

    // ── Write clipboard ───────────────────────────────────────────

    private static async Task<string> WriteClipboardAsync(
        JsonElement parameters, DesktopAwarenessService desktop)
    {
        var text = Str(parameters, "text");
        string[]? filePaths = null;
        if (parameters.TryGetProperty("filePaths", out var fp) &&
            fp.ValueKind == JsonValueKind.Array)
        {
            filePaths = fp.EnumerateArray()
                .Select(e => e.GetString()!)
                .ToArray();
        }

        return await desktop.WriteClipboardAsync(text, filePaths);
    }

    // ── Stop process ──────────────────────────────────────────────

    private static async Task<string> StopProcessAsync(
        JsonElement parameters, AgentJobContext job,
        DesktopAwarenessService desktop, IServiceProvider sp,
        CancellationToken ct)
    {
        var nativeAppService = sp.GetRequiredService<NativeApplicationService>();

        var processId = Int(parameters, "processId")
            ?? throw new InvalidOperationException("stop_process requires 'processId'.");
        var force = Bool(parameters, "force") ?? false;

        NativeApplicationDB? app = null;
        if (job.ResourceId.HasValue)
            app = await nativeAppService.ResolveAsync(
                job.ResourceId.Value.ToString(), ct);

        if (app is null)
        {
            var alias = Str(parameters, "alias");
            if (!string.IsNullOrWhiteSpace(alias))
                app = await nativeAppService.ResolveAsync(alias, ct);
        }

        if (app is null)
            throw new InvalidOperationException(
                "stop_process requires a registered native application (resourceId or alias).");

        return await desktop.StopProcessAsync(processId, force, app, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static async Task<DisplayDeviceResponse> ResolveDisplayAsync(
        Guid? resourceId, IServiceProvider sp, CancellationToken ct)
    {
        if (!resourceId.HasValue)
            throw new InvalidOperationException(
                "This tool requires a ResourceId (DisplayDevice).");

        var service = sp.GetRequiredService<DisplayDeviceService>();
        return await service.GetByIdAsync(resourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"Display device {resourceId} not found.");
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static bool? Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    // ═══════════════════════════════════════════════════════════════
    // JSON Schemas
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement ResourceOnlySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Resource GUID."
                    }
                },
                "required": ["targetId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement GlobalSchema()
    {
        using var doc = JsonDocument.Parse("""{ "type": "object", "properties": {} }""");
        return doc.RootElement.Clone();
    }

    private static JsonElement ClickDesktopSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Display GUID."
                    },
                    "x": {
                        "type": "integer",
                        "description": "X coordinate."
                    },
                    "y": {
                        "type": "integer",
                        "description": "Y coordinate."
                    },
                    "button": {
                        "type": "string",
                        "enum": ["left", "right", "middle"],
                        "description": "Default 'left'."
                    },
                    "clickType": {
                        "type": "string",
                        "enum": ["single", "double"],
                        "description": "Default 'single'."
                    }
                },
                "required": ["targetId", "x", "y"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement TypeOnDesktopSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Display GUID."
                    },
                    "text": {
                        "type": "string",
                        "description": "Text to type."
                    },
                    "x": {
                        "type": "integer",
                        "description": "X click-to-focus."
                    },
                    "y": {
                        "type": "integer",
                        "description": "Y click-to-focus."
                    }
                },
                "required": ["targetId", "text"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement LaunchApplicationSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Native application GUID."
                    },
                    "alias": {
                        "type": "string",
                        "description": "Short alias (e.g. 'excel'). Use targetId or alias."
                    },
                    "arguments": {
                        "type": "string",
                        "description": "Optional command-line arguments."
                    },
                    "filePath": {
                        "type": "string",
                        "description": "Optional file to open with the application."
                    }
                },
                "required": []
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement WindowTargetSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "processId": {
                        "type": "integer",
                        "description": "Window's process ID."
                    },
                    "processName": {
                        "type": "string",
                        "description": "Process name (case-insensitive)."
                    },
                    "titleContains": {
                        "type": "string",
                        "description": "Title substring match (case-insensitive)."
                    }
                }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement ResizeWindowSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "processId": {
                        "type": "integer",
                        "description": "Window's process ID."
                    },
                    "titleContains": {
                        "type": "string",
                        "description": "Title substring match."
                    },
                    "x": { "type": "integer", "description": "New X position." },
                    "y": { "type": "integer", "description": "New Y position." },
                    "width": { "type": "integer", "description": "New width." },
                    "height": { "type": "integer", "description": "New height." },
                    "state": {
                        "type": "string",
                        "enum": ["normal", "minimized", "maximized"],
                        "description": "Window state."
                    }
                }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement SendHotkeySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "keys": {
                        "type": "string",
                        "description": "Shortcut, e.g. 'ctrl+s', 'alt+tab', 'ctrl+shift+p'."
                    },
                    "processId": {
                        "type": "integer",
                        "description": "Optional: focus window by PID first."
                    },
                    "titleContains": {
                        "type": "string",
                        "description": "Optional: focus window by title first."
                    }
                },
                "required": ["keys"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement ReadClipboardSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "format": {
                        "type": "string",
                        "enum": ["text", "files", "image"],
                        "description": "Clipboard format. Omit for auto-detect."
                    }
                }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement WriteClipboardSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "text": {
                        "type": "string",
                        "description": "Text to write to clipboard."
                    },
                    "filePaths": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "File paths to put on clipboard."
                    }
                }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement StopProcessSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "processId": {
                        "type": "integer",
                        "description": "PID of the process to stop."
                    },
                    "force": {
                        "type": "boolean",
                        "description": "Skip graceful close; kill immediately."
                    }
                },
                "required": ["processId"]
            }
            """);
        return doc.RootElement.Clone();
    }
}
