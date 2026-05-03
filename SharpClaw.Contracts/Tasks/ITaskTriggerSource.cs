namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// A trigger source implementation that watches for a particular condition and
/// fires one or more <see cref="ITaskTriggerSourceContext"/> instances when it
/// is satisfied. Implemented by both Core and modules.
/// </summary>
public interface ITaskTriggerSource
{
    /// <summary>
    /// String key for sources that handle a single trigger kind.
    /// The host routes binding rows whose <c>Kind</c> column matches this value.
    /// Sources that handle multiple kinds override <see cref="TriggerKeys"/> instead.
    /// </summary>
    string? TriggerKey => null;

    /// <summary>
    /// All string keys handled by this source. Defaults to a single-element list
    /// derived from <see cref="TriggerKey"/> when non-null, or empty if neither
    /// is overridden (invalid — every source must expose at least one key).
    /// Multi-kind sources override this property directly.
    /// </summary>
    IReadOnlyList<string> TriggerKeys => TriggerKey is not null ? [TriggerKey] : [];

    /// <summary>
    /// Start watching. Called by the host when bindings are loaded or reloaded.
    /// Implementations should be idempotent.
    /// </summary>
    /// <param name="contexts">
    /// All active binding contexts for the kinds this source handles.
    /// </param>
    /// <param name="ct">Cancellation token — cancelled when the host shuts down.</param>
    Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct);

    /// <summary>
    /// Stop watching and release all resources held by this source.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Value persisted into <c>TaskTriggerBindingDB.TriggerValue</c>. Used as the
    /// primary lookup key for binding rows. Should be the most specific
    /// identifier the source dispatches on (route path, host name, metric
    /// name, etc.).
    ///
    /// Default implementation returns <see langword="null"/>; while the
    /// trigger-extraction migration is in progress the legacy switch in
    /// <c>TaskTriggerRegistrar</c> remains the source of truth for sources
    /// that have not yet overridden this method.
    /// </summary>
    string? GetBindingValue(TaskTriggerDefinition def) => null;

    /// <summary>
    /// Value persisted into <c>TaskTriggerBindingDB.Filter</c>. Used as a
    /// secondary discriminator (for example, an event-subtype filter on top
    /// of a primary event-type binding value).
    /// </summary>
    string? GetBindingFilter(TaskTriggerDefinition def) => null;

    /// <summary>
    /// Indicates this source manages its own persistence for incoming
    /// trigger definitions. When <see langword="true"/>, the registrar
    /// skips its default <c>TaskTriggerBindingDB</c> upsert for triggers
    /// whose <c>TriggerKey</c> is owned by this source and instead calls
    /// <see cref="SyncBindingsAsync"/>. Sources that opt in must
    /// idempotently persist whatever state they need (e.g.
    /// <c>ScheduledJobDB</c> rows, on-disk shortcut files) and remove
    /// stale state on each call.
    /// </summary>
    /// <remarks>
    /// A source must not implement both <c>OwnsBindingPersistence == true</c>
    /// and <see cref="ITaskTriggerBindingSideEffect"/>: when the source owns
    /// persistence, the default binding rows are not written, so per-row
    /// side-effect hooks have nothing to attach to.
    /// </remarks>
    bool OwnsBindingPersistence => false;

    /// <summary>
    /// Called by <c>TaskTriggerRegistrar</c> when a task definition is
    /// created or updated, but only for sources where
    /// <see cref="OwnsBindingPersistence"/> is <see langword="true"/>.
    /// Receives only the triggers whose <c>TriggerKey</c> this source
    /// claims (via <see cref="TriggerKeys"/>). Implementations must be
    /// idempotent: stale state for the same definition must be removed
    /// in the same call.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if any tracked state changed, so the
    /// registrar can propagate the change flag to its caller.
    /// </returns>
    Task<bool> SyncBindingsAsync(
        TaskDefinitionDescriptor definition,
        IReadOnlyList<TaskTriggerDefinition> ownedTriggers,
        CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// Called by <c>TaskTriggerRegistrar</c> when a task definition is
    /// deleted, but only for sources where <see cref="OwnsBindingPersistence"/>
    /// is <see langword="true"/>. Implementations must remove all state
    /// previously persisted for <paramref name="definitionId"/>.
    /// </summary>
    Task RemoveBindingsAsync(Guid definitionId, CancellationToken ct) =>
        Task.CompletedTask;
}
