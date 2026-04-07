using SharpClaw.Contracts.DTOs.AgentActions;

namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Declares permission requirements for a module tool.
/// The pipeline uses this to evaluate access before executing the tool.
/// </summary>
public sealed record ModuleToolPermission(
    /// <summary>Whether this tool requires a ResourceId.</summary>
    bool IsPerResource,

    /// <summary>
    /// Permission callback. Receives the agent ID, optional resource ID,
    /// and caller, and returns the standard <see cref="AgentActionResult"/>.
    /// If <c>null</c>, delegates to a named AgentActionService method.
    /// </summary>
    Func<Guid, Guid?, ActionCaller, CancellationToken, Task<AgentActionResult>>? Check,

    /// <summary>
    /// If <see cref="Check"/> is <c>null</c>, the name of the AgentActionService method to call.
    /// This supports modules that reuse existing permission categories
    /// (e.g. a module tool that requires the same permission as ClickDesktop).
    /// Validated at registration time.
    /// </summary>
    string? DelegateTo = null
);
