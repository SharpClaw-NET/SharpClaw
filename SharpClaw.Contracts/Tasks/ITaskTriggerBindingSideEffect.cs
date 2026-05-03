namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Optional companion contract to <see cref="ITaskTriggerSource"/> for
/// trigger kinds that need a side effect attached to each persisted
/// binding row but do not own binding persistence themselves
/// (i.e. the source still wants the registrar's default
/// <c>TaskTriggerBindingDB</c> upsert).
///
/// The canonical example is the OS-shortcut trigger: the binding row is
/// indistinguishable from any other trigger binding, but creating one
/// must also write a <c>.lnk</c> / <c>.desktop</c> file, and removing
/// one must delete it again.
/// </summary>
/// <remarks>
/// A trigger source must not implement both <c>ITaskTriggerSource</c> with
/// <see cref="ITaskTriggerSource.OwnsBindingPersistence"/> set to
/// <see langword="true"/> and this side-effect contract: when the source
/// owns persistence, the registrar does not produce the binding rows that
/// these hooks would otherwise observe.
/// </remarks>
public interface ITaskTriggerBindingSideEffect
{
    /// <summary>
    /// Trigger key this side effect attaches to. Compared against
    /// <c>TaskTriggerBindingDB.Kind</c> on each registrar pass.
    /// </summary>
    string TriggerKey { get; }

    /// <summary>
    /// Fires after the registrar persists a binding row for a trigger
    /// matching <see cref="TriggerKey"/>. Implementations should be
    /// idempotent — the registrar may re-fire this hook on retries.
    /// </summary>
    Task OnBindingCreatedAsync(
        TaskDefinitionDescriptor definition,
        TaskTriggerDefinition trigger,
        TaskTriggerBindingDescriptor binding,
        CancellationToken ct);

    /// <summary>
    /// Fires before the registrar removes a binding row for a trigger
    /// matching <see cref="TriggerKey"/>. Implementations should be
    /// idempotent — the side effect must remain a no-op if the underlying
    /// state was already cleared.
    /// </summary>
    Task OnBindingRemovedAsync(
        TaskTriggerBindingDescriptor binding,
        CancellationToken ct);
}
