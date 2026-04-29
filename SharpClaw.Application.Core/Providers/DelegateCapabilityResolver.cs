using SharpClaw.Contracts.Providers;

namespace SharpClaw.Application.Core.Providers;

/// <summary>
/// Functional <see cref="IModelCapabilityResolver"/> that delegates to a
/// plain delegate. Used by the in-Core transitional plugins until each
/// provider module owns its own resolver implementation (Phase 4).
/// </summary>
public sealed class DelegateCapabilityResolver(Func<string, HashSet<string>> resolve)
    : IModelCapabilityResolver
{
    public HashSet<string> Resolve(string modelName) => resolve(modelName);
}
