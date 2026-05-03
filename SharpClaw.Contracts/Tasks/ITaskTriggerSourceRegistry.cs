namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Read-only facade over the set of <see cref="ITaskTriggerSource"/>
/// implementations registered with the host. Introduced by the trigger
/// extraction plan so that <c>TaskTriggerRegistrar</c> can resolve a source
/// by its trigger key and delegate binding-value derivation to it instead
/// of switching on well-known core key strings.
/// </summary>
public interface ITaskTriggerSourceRegistry
{
    /// <summary>
    /// Resolve the trigger source that handles the given trigger key, or
    /// <see langword="null"/> if no registered source claims the key
    /// (for example because the owning module is currently disabled).
    /// </summary>
    ITaskTriggerSource? ResolveByKey(string? triggerKey);

    /// <summary>All trigger sources currently registered with the host.</summary>
    IReadOnlyList<ITaskTriggerSource> Sources { get; }

    /// <summary>
    /// Resolve the binding side-effect handler that attaches to the given
    /// trigger key, or <see langword="null"/> if no registered side effect
    /// claims the key. Distinct from <see cref="ResolveByKey(string?)"/>
    /// because side effects are an opt-in companion to a trigger source —
    /// most sources don't need one.
    /// </summary>
    ITaskTriggerBindingSideEffect? ResolveSideEffect(string? triggerKey);

    /// <summary>All binding side-effect handlers currently registered.</summary>
    IReadOnlyList<ITaskTriggerBindingSideEffect> SideEffects { get; }
}
