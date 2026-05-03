using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Application.Services;

/// <summary>
/// Default <see cref="IChatProcessingBridge"/> implementation.
/// Fans out to every registered <see cref="IChatProcessingContributor"/>,
/// aggregates their contributions, and de-duplicates results so core
/// (<see cref="ChatService"/>, <see cref="HeaderTagProcessor"/>) never
/// names a module-owned permission key inline.
///
/// Modules contribute by registering their own
/// <see cref="IChatProcessingContributor"/> in DI; this aggregator picks
/// them up automatically through <c>IEnumerable&lt;IChatProcessingContributor&gt;</c>.
/// </summary>
public sealed class ChatProcessingBridge(
    IEnumerable<IChatProcessingContributor> contributors) : IChatProcessingBridge
{
    public async Task<IReadOnlyList<ChatToolDefinition>> GetExtraToolsAsync(
        Guid agentId, CancellationToken ct = default)
    {
        var aggregated = new List<ChatToolDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contributor in contributors)
        {
            var tools = await contributor.GetExtraToolsAsync(agentId, ct);
            foreach (var tool in tools)
            {
                if (seen.Add(tool.Name))
                    aggregated.Add(tool);
            }
        }
        return aggregated;
    }

    public async Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId, Guid currentChannelId, CancellationToken ct = default)
    {
        var aggregated = new List<ThreadSummary>();
        var seen = new HashSet<Guid>();
        foreach (var contributor in contributors)
        {
            var threads = await contributor.GetAccessibleThreadsAsync(
                agentId, currentChannelId, ct);
            foreach (var t in threads)
            {
                if (seen.Add(t.ThreadId))
                    aggregated.Add(t);
            }
        }
        return aggregated;
    }
}
