using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Contracts.Chat;

/// <summary>
/// Bridge surface used by the host's chat pipeline (<c>ChatService</c>,
/// <c>HeaderTagProcessor</c>) to delegate module-owned decisions back into
/// modules without taking a direct dependency on Core or Infrastructure
/// types.
///
/// Implemented in <c>SharpClaw.Application.Core</c> as a host aggregator
/// over all registered <see cref="IChatProcessingContributor"/>s; modules
/// register a contributor to participate in the chat processing pipeline.
/// Each method maps 1:1 to a module-specific concern previously hard-coded
/// inside core call sites (CanInvokeTasksAsTool, CanReadCrossThreadHistory).
/// </summary>
public interface IChatProcessingBridge
{
    /// <summary>
    /// Returns extra <see cref="ChatToolDefinition"/> entries contributed by
    /// modules for the current chat turn (e.g. agent-orchestration exposes
    /// active task definitions when the agent has CanInvokeTasksAsTool).
    /// Aggregates results from every registered contributor.
    /// </summary>
    Task<IReadOnlyList<ChatToolDefinition>> GetExtraToolsAsync(
        Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// Returns threads accessible to the agent from channels other than
    /// <paramref name="currentChannelId"/>, applying each contributor's
    /// own cross-thread visibility policy. Aggregates and de-duplicates
    /// results from every registered contributor.
    /// </summary>
    Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId, Guid currentChannelId, CancellationToken ct = default);
}
