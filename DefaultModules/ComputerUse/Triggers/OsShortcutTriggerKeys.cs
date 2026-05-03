namespace SharpClaw.Modules.ComputerUse.Triggers;

/// <summary>
/// Module-owned trigger-key and parameter-name constants for the
/// <c>OsShortcut</c> trigger. The string values must remain byte-identical
/// to the legacy core constants — they are persisted into
/// <c>TaskTriggerBindingDB.Kind</c> and into
/// <c>TaskTriggerDefinition.Parameters</c> keys, and existing serialized
/// task scripts must round-trip without rewrites.
/// </summary>
public static class OsShortcutTriggerKeys
{
    /// <summary>Trigger key persisted in <c>TaskTriggerBindingDB.Kind</c>.</summary>
    public const string OsShortcut = "OsShortcut";

    /// <summary>
    /// <c>TaskTriggerDefinition.Parameters</c> key for the user-visible
    /// shortcut label (window title / desktop entry <c>Name</c>).
    /// </summary>
    public const string ShortcutLabel = "ShortcutLabel";

    /// <summary>
    /// <c>TaskTriggerDefinition.Parameters</c> key for the icon path or
    /// stock icon name passed to the OS shell when registering the shortcut.
    /// </summary>
    public const string ShortcutIcon = "ShortcutIcon";

    /// <summary>
    /// <c>TaskTriggerDefinition.Parameters</c> key for the freedesktop /
    /// Start menu category the shortcut appears under.
    /// </summary>
    public const string ShortcutCategory = "ShortcutCategory";
}
