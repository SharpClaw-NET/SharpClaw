using SharpClaw.Contracts.Providers;

namespace SharpClaw.Providers.Common;

/// <summary>
/// Functional <see cref="IModelCapabilityResolver"/> that delegates to a
/// plain delegate. Convenience wrapper for plugins that compose a resolver
/// from a method group rather than a dedicated type.
/// </summary>
public sealed class DelegateCapabilityResolver(Func<string, HashSet<string>> resolve)
    : IModelCapabilityResolver
{
    public HashSet<string> Resolve(string modelName) => resolve(modelName);
}
