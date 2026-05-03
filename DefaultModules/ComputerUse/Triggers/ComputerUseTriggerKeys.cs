namespace SharpClaw.Modules.ComputerUse.Triggers;

/// <summary>
/// Trigger and parameter keys owned by the computer-use module's desktop
/// trigger family (process, window-focus, hotkey, idle, screen-lock,
/// device). String values mirror the legacy <c>TaskTriggerDefinition</c>
/// property names verbatim so persisted binding rows and serialized
/// scripts continue to round-trip after the typed-property surface is
/// removed.
/// </summary>
public static class ComputerUseTriggerKeys
{
    // Trigger keys persisted in TaskTriggerBindingDB.Kind.
    public const string ProcessStarted   = "ProcessStarted";
    public const string ProcessStopped   = "ProcessStopped";
    public const string WindowFocused    = "WindowFocused";
    public const string WindowBlurred    = "WindowBlurred";
    public const string Hotkey           = "Hotkey";
    public const string SystemIdle       = "SystemIdle";
    public const string SystemActive     = "SystemActive";
    public const string ScreenLocked     = "ScreenLocked";
    public const string ScreenUnlocked   = "ScreenUnlocked";
    public const string DeviceConnected  = "DeviceConnected";
    public const string DeviceDisconnected = "DeviceDisconnected";

    // Parameter names — must match the legacy TaskTriggerDefinition
    // property names so existing on-disk JSON keeps deserialising.
    public const string ProcessName       = "ProcessName";
    public const string HotkeyCombo       = "HotkeyCombo";
    public const string IdleMinutes       = "IdleMinutes";
    public const string DeviceClass       = "DeviceClass";
    public const string DeviceNamePattern = "DeviceNamePattern";
}
