using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Core.Services.Triggers;

/// <summary>
/// Default <see cref="ITaskTriggerSourceRegistry"/> implementation. Resolves
/// a trigger source by matching its <see cref="ITaskTriggerSource.TriggerKeys"/>
/// (case-insensitive) against the requested key.
/// </summary>
public sealed class TaskTriggerSourceRegistry(
    IEnumerable<ITaskTriggerSource> sources,
    IEnumerable<ITaskTriggerBindingSideEffect>? sideEffects = null)
    : ITaskTriggerSourceRegistry
{
    public IReadOnlyList<ITaskTriggerSource> Sources { get; } =
        sources.ToList().AsReadOnly();

    public IReadOnlyList<ITaskTriggerBindingSideEffect> SideEffects { get; } =
        (sideEffects ?? []).ToList().AsReadOnly();

    public ITaskTriggerSource? ResolveByKey(string? triggerKey)
    {
        if (string.IsNullOrWhiteSpace(triggerKey)) return null;

        foreach (var src in Sources)
        {
            foreach (var key in src.TriggerKeys)
            {
                if (string.Equals(key, triggerKey, StringComparison.OrdinalIgnoreCase))
                    return src;
            }
        }

        return null;
    }

    public ITaskTriggerBindingSideEffect? ResolveSideEffect(string? triggerKey)
    {
        if (string.IsNullOrWhiteSpace(triggerKey)) return null;

        foreach (var sideEffect in SideEffects)
        {
            if (string.Equals(sideEffect.TriggerKey, triggerKey, StringComparison.OrdinalIgnoreCase))
                return sideEffect;
        }

        return null;
    }
}
