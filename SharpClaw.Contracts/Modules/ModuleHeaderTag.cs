namespace SharpClaw.Contracts.Modules;

/// <summary>
/// A custom header tag provided by a module.
/// When the tag placeholder appears in a custom chat header,
/// <c>HeaderTagProcessor</c> calls the resolver to expand it.
/// </summary>
public sealed record ModuleHeaderTag(
    /// <summary>Tag name without braces (e.g. "active_windows").</summary>
    string Name,

    /// <summary>
    /// Async resolver called by <c>HeaderTagProcessor</c> during header expansion.
    /// Receives the scoped <see cref="IServiceProvider"/> and returns the replacement string.
    /// </summary>
    Func<IServiceProvider, CancellationToken, Task<string>> Resolve
)
{
    /// <summary>
    /// Optional context-aware resolver for tags that depend on the current
    /// channel, agent, user, client, or provider request. When set, the host
    /// uses this resolver instead of <see cref="Resolve"/>.
    /// </summary>
    public Func<IServiceProvider, ModuleHeaderTagContext, CancellationToken, Task<string>>?
        ResolveWithContext { get; init; }
}
