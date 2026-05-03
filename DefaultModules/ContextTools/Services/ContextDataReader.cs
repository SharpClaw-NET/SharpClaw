using Microsoft.EntityFrameworkCore;

using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Modules.ContextTools.Services;

/// <summary>
/// A single chat message returned by <see cref="ContextDataReader.GetThreadMessagesAsync"/>.
/// Module-internal record; never crosses the host boundary.
/// </summary>
internal sealed record ChatMessageSummary(
    string Role,
    string Content,
    string Sender,
    DateTimeOffset Timestamp);

/// <summary>
/// Module-owned read service that backs the ContextTools inline tools.
/// Reads directly from the host's <see cref="ISharpClawDataContext"/>
/// surface so the cross-thread visibility policy and the
/// <c>CanReadCrossThreadHistory</c> permission key live in the module
/// that owns them.
/// </summary>
internal sealed class ContextDataReader(ISharpClawDataContext data)
{
    public Task<bool> ThreadExistsAsync(Guid threadId, Guid channelId, CancellationToken ct = default) =>
        data.ChatThreads.AnyAsync(t => t.Id == threadId && t.ChannelId == channelId, ct);

    public async Task<IReadOnlyList<ChatMessageSummary>> GetThreadMessagesAsync(
        Guid threadId, int maxMessages, CancellationToken ct = default)
    {
        return await data.ChatMessages
            .Where(m => m.ThreadId == threadId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxMessages)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessageSummary(
                m.Role,
                m.Content,
                m.SenderUsername ?? m.SenderAgentName ?? "unknown",
                m.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId, Guid currentChannelId, CancellationToken ct = default)
    {
        var agentWithRole = await data.Agents
            .Include(a => a.Role)
                .ThenInclude(r => r!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        var agentPs = agentWithRole?.Role?.PermissionSet;
        var crossThreadKey = ContextToolsPermissionKeys.CanReadCrossThreadHistory;
        if (agentPs is null || !agentPs.GlobalFlags.Any(f => f.FlagKey == crossThreadKey))
            return [];

        var isIndependent = (agentPs.GlobalFlags
            .FirstOrDefault(f => f.FlagKey == crossThreadKey)
            ?.Clearance ?? PermissionClearance.Unset) == PermissionClearance.Independent;

        var channels = await data.Channels
            .Include(c => c.AllowedAgents)
            .Include(c => c.PermissionSet)
                .ThenInclude(ps => ps!.GlobalFlags)
            .Include(c => c.AgentContext)
                .ThenInclude(ctx => ctx!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .Where(c => c.Id != currentChannelId)
            .Where(c => c.AgentId == agentId || c.AllowedAgents.Any(a => a.Id == agentId))
            .ToListAsync(ct);

        if (!isIndependent)
        {
            channels = channels
                .Where(c =>
                {
                    var effectivePs = c.PermissionSet ?? c.AgentContext?.PermissionSet;
                    return effectivePs?.GlobalFlags.Any(f => f.FlagKey == crossThreadKey) == true;
                })
                .ToList();
        }

        if (channels.Count == 0)
            return [];

        var channelIds = channels.Select(c => c.Id).ToList();
        var channelTitles = channels.ToDictionary(c => c.Id, c => c.Title);

        return await data.ChatThreads
            .Where(t => channelIds.Contains(t.ChannelId))
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new ThreadSummary(t.Id, t.Name, t.ChannelId, channelTitles[t.ChannelId]))
            .ToListAsync(ct);
    }
}
