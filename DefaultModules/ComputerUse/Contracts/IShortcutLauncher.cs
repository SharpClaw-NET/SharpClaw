using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.ComputerUse.Contracts;

/// <summary>
/// Module-owned contract for OS shortcut creation and removal.
/// The launcher reads shortcut metadata
/// (<c>ShortcutLabel</c>, <c>ShortcutIcon</c>, <c>ShortcutCategory</c>)
/// from <see cref="TaskTriggerDefinition.Parameters"/> using
/// <see cref="Triggers.OsShortcutTriggerKeys"/> rather than from the
/// typed shim properties on <see cref="TaskTriggerDefinition"/>.
/// </summary>
public interface IShortcutLauncher
{
    /// <summary>
    /// Writes the OS shortcut (.lnk / .desktop) and stub launcher for the
    /// given trigger definition.
    /// </summary>
    Task WriteShortcutAsync(TaskTriggerDefinition definition, string customId, CancellationToken ct = default);

    /// <summary>
    /// Rewrites the shortcut file for an existing task (label, icon, category).
    /// Leaves the stub launcher untouched.
    /// </summary>
    Task RefreshShortcutsAsync(TaskTriggerDefinition definition, string customId, CancellationToken ct = default);

    /// <summary>
    /// Deletes all shortcut files and the stub launcher for <paramref name="customId"/>.
    /// </summary>
    Task RemoveShortcutsAsync(string customId, CancellationToken ct = default);
}
