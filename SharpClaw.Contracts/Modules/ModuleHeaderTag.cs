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
);
