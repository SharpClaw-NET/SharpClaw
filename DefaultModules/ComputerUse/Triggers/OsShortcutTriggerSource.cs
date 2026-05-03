using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.ComputerUse.Contracts;

namespace SharpClaw.Modules.ComputerUse.Triggers;

/// <summary>
/// Module-owned trigger source for <c>OsShortcut</c>. The OS shortcut
/// trigger is unusual in that the persisted binding row itself looks
/// identical to any other trigger binding — the registrar's default
/// <c>TaskTriggerBindingDB</c> upsert path handles persistence — but
/// creating or removing a binding must also write or delete an OS-level
/// shortcut file (.lnk on Windows, .desktop on Linux).
///
/// The source therefore opts out of binding-persistence ownership
/// (<see cref="ITaskTriggerSource.OwnsBindingPersistence"/> is left at
/// the default <see langword="false"/>) and instead implements
/// <see cref="ITaskTriggerBindingSideEffect"/> so the registrar fires
/// the file-side action immediately after each binding row is added or
/// removed.
/// </summary>
public sealed class OsShortcutTriggerSource(
    IShortcutLauncher launcher,
    ILogger<OsShortcutTriggerSource> logger)
    : ITaskTriggerSource, ITaskTriggerBindingSideEffect
{
    /// <inheritdoc />
    public string TriggerKey => OsShortcutTriggerKeys.OsShortcut;

    /// <inheritdoc />
    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public string? GetBindingValue(TaskTriggerDefinition def)
    {
        if (def.Parameters.TryGetValue(OsShortcutTriggerKeys.ShortcutLabel, out var label) &&
            !string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        return null;
    }

    // Explicit interface implementation: TriggerKey is shared with
    // ITaskTriggerSource and identical, so we just route to it.
    string ITaskTriggerBindingSideEffect.TriggerKey => TriggerKey;

    /// <inheritdoc />
    public async Task OnBindingCreatedAsync(
        TaskDefinitionDescriptor definition,
        TaskTriggerDefinition trigger,
        TaskTriggerBindingDescriptor binding,
        CancellationToken ct)
    {
        var customId = definition.Name;
        if (string.IsNullOrWhiteSpace(customId))
        {
            logger.LogWarning(
                "OsShortcut binding for definition {DefinitionId} has no task name; skipping shortcut write.",
                definition.Id);
            return;
        }

        await launcher.WriteShortcutAsync(trigger, customId, ct);
    }

    /// <inheritdoc />
    public async Task OnBindingRemovedAsync(
        TaskTriggerBindingDescriptor binding,
        CancellationToken ct)
    {
        var customId = binding.TriggerValue;
        if (string.IsNullOrWhiteSpace(customId))
        {
            customId = binding.TaskDefinitionId.ToString();
        }

        await launcher.RemoveShortcutsAsync(customId, ct);
    }
}
