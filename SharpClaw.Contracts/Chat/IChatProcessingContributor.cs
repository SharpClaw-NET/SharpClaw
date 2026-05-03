using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Contracts.Chat;

/// <summary>
/// Module-side hook for participating in core chat processing. A module
/// (or a host adapter on its behalf) implements this to inject extra tools
/// or extra accessible threads into a chat turn without core having to
/// know about the module's permissions or wire-level flag keys.
///
/// All methods are optional; the default implementations return empty
/// collections so contributors only override what they actually contribute.
/// Registered as scoped services; the host aggregator
/// (<see cref="IChatProcessingBridge"/>) fans out to every registration.
/// </summary>
public interface IChatProcessingContributor
{
    /// <summary>
    /// Contribute extra <see cref="ChatToolDefinition"/> entries for this
    /// chat turn. Implementations are responsible for evaluating their own
    /// permissions before returning anything.
    /// </summary>
    Task<IReadOnlyList<ChatToolDefinition>> GetExtraToolsAsync(
        Guid agentId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChatToolDefinition>>([]);

    /// <summary>
    /// Contribute threads from other channels that the agent should be
    /// able to read. Implementations apply their own policy (clearance,
    /// channel opt-in, permission flags) before returning.
    /// </summary>
    Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId, Guid currentChannelId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ThreadSummary>>([]);
}
