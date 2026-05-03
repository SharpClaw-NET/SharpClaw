using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.ContextTools.Services;

namespace SharpClaw.Modules.ContextTools;

/// <summary>
/// Module-owned <see cref="IChatProcessingContributor"/> that surfaces
/// the ContextTools cross-thread visibility policy to the host chat
/// pipeline. The permission key (<c>CanReadCrossThreadHistory</c>) and
/// the underlying query both live in this module; core only invokes
/// <see cref="IChatProcessingBridge"/> and never names the wire string.
/// </summary>
internal sealed class ContextToolsChatContributor(ContextDataReader dataReader)
    : IChatProcessingContributor
{
    public Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId, Guid currentChannelId, CancellationToken ct = default)
        => dataReader.GetAccessibleThreadsAsync(agentId, currentChannelId, ct);
}
