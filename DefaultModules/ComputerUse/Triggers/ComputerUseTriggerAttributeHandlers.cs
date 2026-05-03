using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.ComputerUse.Triggers;

/// <summary>
/// Module-owned <see cref="ITaskTriggerAttributeHandler"/> implementations
/// for the desktop / OS trigger-attribute family claimed by
/// <c>sharpclaw_computer_use</c>:
/// <c>[OnProcessStarted]</c>, <c>[OnProcessStopped]</c>,
/// <c>[OnWindowFocused]</c>, <c>[OnWindowBlurred]</c>, <c>[OnHotkey]</c>,
/// <c>[OnSystemIdle]</c>, <c>[OnSystemActive]</c>, <c>[OnScreenLocked]</c>,
/// <c>[OnScreenUnlocked]</c>, <c>[OnDeviceConnected]</c>,
/// <c>[OnDeviceDisconnected]</c>, and <c>[OsShortcut]</c>.
/// <para>
/// Behavior preserved verbatim from the legacy core parser switch,
/// including TASK420 (macOS-incompatible attribute warning) and TASK429
/// (hotkey combination validation).
/// </para>
/// </summary>
internal static class ComputerUseTriggerAttributeHandlers
{
    public static IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> All { get; } =
        new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal)
        {
            ["OnProcessStarted"]    = new ProcessHandler("ProcessStarted"),
            ["OnProcessStopped"]    = new ProcessHandler("ProcessStopped"),
            ["OnWindowFocused"]     = new WindowHandler("WindowFocused"),
            ["OnWindowBlurred"]     = new WindowHandler("WindowBlurred"),
            ["OnHotkey"]            = new HotkeyHandler(),
            ["OnSystemIdle"]        = new SystemIdleHandler(),
            ["OnSystemActive"]      = new MacIncompatibleHandler("SystemActive"),
            ["OnScreenLocked"]      = new MacIncompatibleHandler("ScreenLocked"),
            ["OnScreenUnlocked"]    = new MacIncompatibleHandler("ScreenUnlocked"),
            ["OnDeviceConnected"]   = new DeviceHandler("DeviceConnected"),
            ["OnDeviceDisconnected"]= new DeviceHandler("DeviceDisconnected"),
            ["OsShortcut"]          = new OsShortcutHandler(),
        };

    private static void EmitMacWarning(TaskTriggerAttributeContext ctx)
    {
        ctx.Report(
            TaskTriggerAttributeDiagnosticSeverity.Warning,
            "TASK420",
            $"[{ctx.AttributeName}] is not supported on macOS and will be ignored at runtime on that platform.");
    }

    private static TaskTriggerDefinition WithProcess(string triggerKey, TaskTriggerAttributeContext context)
    {
        var p = new Dictionary<string, string?>(StringComparer.Ordinal);
        var name = context.GetStringArg(0);
        if (!string.IsNullOrEmpty(name))
            p[ComputerUseTriggerKeys.ProcessName] = name;
        return new TaskTriggerDefinition
        {
            TriggerKey = triggerKey,
            Parameters = p,
        };
    }

    private sealed class ProcessHandler(string triggerKey) : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            EmitMacWarning(context);
            return WithProcess(triggerKey, context);
        }
    }

    private sealed class WindowHandler(string triggerKey) : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            EmitMacWarning(context);
            return WithProcess(triggerKey, context);
        }
    }

    private sealed class MacIncompatibleHandler(string triggerKey) : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            EmitMacWarning(context);
            return new TaskTriggerDefinition { TriggerKey = triggerKey };
        }
    }

    private sealed class HotkeyHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            EmitMacWarning(context);
            var combo = context.GetStringArg(0);
            if (!IsHotkeyComboValid(combo))
            {
                context.Report(
                    TaskTriggerAttributeDiagnosticSeverity.Error,
                    "TASK429",
                    $"[OnHotkey] key combination \"{combo}\" could not be parsed. " +
                    "Expected format: \"Modifier+Key\" (e.g. \"Ctrl+Shift+F10\").");
            }
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(combo))
                p[ComputerUseTriggerKeys.HotkeyCombo] = combo;
            return new TaskTriggerDefinition
            {
                TriggerKey = ComputerUseTriggerKeys.Hotkey,
                Parameters = p,
            };
        }
    }

    private sealed class SystemIdleHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            EmitMacWarning(context);
            var minutes = context.GetNamedIntArg("Minutes") ?? context.GetIntArg(0);
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (minutes.HasValue)
                p[ComputerUseTriggerKeys.IdleMinutes] = minutes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new TaskTriggerDefinition
            {
                TriggerKey = ComputerUseTriggerKeys.SystemIdle,
                Parameters = p,
            };
        }
    }

    private sealed class DeviceHandler(string triggerKey) : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var cls = context.GetNamedStringArg("Class");
            if (!string.IsNullOrEmpty(cls))
                p[ComputerUseTriggerKeys.DeviceClass] = cls;
            var pattern = context.GetNamedStringArg("Pattern");
            if (!string.IsNullOrEmpty(pattern))
                p[ComputerUseTriggerKeys.DeviceNamePattern] = pattern;
            return new TaskTriggerDefinition
            {
                TriggerKey = triggerKey,
                Parameters = p,
            };
        }
    }

    private sealed class OsShortcutHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var label = context.GetStringArg(0);
            if (!string.IsNullOrEmpty(label))
                p[OsShortcutTriggerKeys.ShortcutLabel] = label;
            var icon = context.GetNamedStringArg("Icon");
            if (!string.IsNullOrEmpty(icon))
                p[OsShortcutTriggerKeys.ShortcutIcon] = icon;
            var category = context.GetNamedStringArg("Category");
            if (!string.IsNullOrEmpty(category))
                p[OsShortcutTriggerKeys.ShortcutCategory] = category;
            return new TaskTriggerDefinition
            {
                TriggerKey = OsShortcutTriggerKeys.OsShortcut,
                Parameters = p,
            };
        }
    }

    // ── Hotkey validation (verbatim from TaskScriptParser) ────────

    private static readonly HashSet<string> KnownModifiers =
        ["Ctrl", "Alt", "Shift", "Win", "Meta", "Control", "Windows"];

    private static readonly HashSet<string> KnownKeys =
        ["F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12",
         "A","B","C","D","E","F","G","H","I","J","K","L","M",
         "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
         "0","1","2","3","4","5","6","7","8","9",
         "Space","Enter","Tab","Escape","Backspace","Delete","Insert",
         "Home","End","PageUp","PageDown","Up","Down","Left","Right",
         "NumPad0","NumPad1","NumPad2","NumPad3","NumPad4",
         "NumPad5","NumPad6","NumPad7","NumPad8","NumPad9",
         "Multiply","Add","Subtract","Divide","Decimal",
         "OemSemicolon","OemPlus","OemComma","OemMinus","OemPeriod",
         "OemOpenBrackets","OemCloseBrackets","OemPipe","OemQuotes","OemBackslash",
         "PrintScreen","Pause","ScrollLock","CapsLock","NumLock"];

    private static bool IsHotkeyComboValid(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo))
            return false;

        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!KnownModifiers.Contains(parts[i]))
                return false;
        }

        return KnownKeys.Contains(parts[^1]);
    }
}
