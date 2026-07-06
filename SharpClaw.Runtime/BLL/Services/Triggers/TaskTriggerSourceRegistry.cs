using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Modules;

namespace SharpClaw.Runtime.BLL.Services.Triggers;

/// <summary>
/// Default <see cref="ITaskTriggerSourceRegistry"/> implementation. Resolves
/// a trigger source by matching its <see cref="ITaskTriggerSource.TriggerKeys"/>
/// (case-insensitive) against the requested key.
/// </summary>
public sealed class TaskTriggerSourceRegistry(
    IEnumerable<ITaskTriggerSource> sources,
    IEnumerable<ITaskTriggerBindingSideEffect>? sideEffects = null,
    ModuleRegistry? moduleRegistry = null)
    : ITaskTriggerSourceRegistry
{
    private readonly IReadOnlyList<ITaskTriggerSource> _sources =
        sources.ToList().AsReadOnly();

    private readonly IReadOnlyList<ITaskTriggerBindingSideEffect> _sideEffects =
        (sideEffects ?? []).ToList().AsReadOnly();

    public IReadOnlyList<ITaskTriggerSource> Sources =>
        [.. _sources.Concat(GetExternalServices<ITaskTriggerSource>())];

    public IReadOnlyList<ITaskTriggerBindingSideEffect> SideEffects =>
        [.. _sideEffects.Concat(GetExternalServices<ITaskTriggerBindingSideEffect>())];

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

    private IEnumerable<T> GetExternalServices<T>()
        where T : class
    {
        if (moduleRegistry is null)
            yield break;

        foreach (var host in moduleRegistry.GetRuntimeHosts())
        {
            foreach (var service in host.Services.GetServices<T>())
                yield return service;
        }
    }
}
