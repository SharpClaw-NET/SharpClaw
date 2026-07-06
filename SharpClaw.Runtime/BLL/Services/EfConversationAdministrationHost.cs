using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Core.Conversation;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfConversationAdministrationHost(
    SharpClawDbContext db,
    IConfiguration configuration,
    ChatCache chatCache) : IConversationAdministrationHost
{
    public bool UniqueChannelNamesEnforced =>
        ConversationTopologyEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Channels"]);

    public bool UniqueContextNamesEnforced =>
        ConversationTopologyEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Contexts"]);

    public async Task<AgentDB?> LoadAgentAsync(
        Guid agentId,
        CancellationToken ct)
    {
        return await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);
    }

    public async Task<IReadOnlyList<AgentDB>> LoadAgentsAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken ct)
    {
        return await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .Where(a => agentIds.Contains(a.Id))
            .ToListAsync(ct);
    }

    public async Task<ChannelDB?> LoadChannelAsync(
        Guid channelId,
        CancellationToken ct)
    {
        return await ChannelsWithDetails()
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
    }

    public async Task<ChannelContextDB?> LoadContextAsync(
        Guid contextId,
        CancellationToken ct)
    {
        return await ContextsWithDetails()
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);
    }

    public async Task<ChatThreadDB?> LoadThreadAsync(
        Guid threadId,
        CancellationToken ct)
    {
        return await db.ChatThreads.FindAsync([threadId], ct);
    }

    public async Task<bool> ChannelExistsAsync(
        Guid channelId,
        CancellationToken ct)
    {
        return await db.Channels.AnyAsync(c => c.Id == channelId, ct);
    }

    public async Task<IReadOnlyList<ChannelDB>> ListChannelsAsync(
        Guid? agentId,
        Guid? contextId,
        CancellationToken ct)
    {
        var query = ChannelsWithDetails();

        if (agentId is not null)
            query = query.Where(c => c.AgentId == agentId);

        if (contextId is not null)
            query = query.Where(c => c.AgentContextId == contextId);

        return await query.ToListAsync(ct);
    }

    public async Task<Guid?> LoadLatestMessageChannelIdAsync(CancellationToken ct)
    {
        return await db.ChatMessages
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => (Guid?)m.ChannelId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ChannelDB?> LoadMostRecentlyCreatedChannelAsync(
        CancellationToken ct)
    {
        return await ChannelsWithDetails()
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ChannelContextDB>> ListContextsAsync(
        Guid? agentId,
        CancellationToken ct)
    {
        var query = ContextsWithDetails();

        if (agentId is not null)
            query = query.Where(c => c.AgentId == agentId);

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChatThreadDB>> ListThreadsAsync(
        Guid channelId,
        CancellationToken ct)
    {
        return await db.ChatThreads
            .Where(t => t.ChannelId == channelId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListChannelTitlesAsync(
        Guid? excludeId,
        CancellationToken ct)
    {
        return await db.Channels
            .Where(c => excludeId == null || c.Id != excludeId)
            .Select(c => c.Title)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListContextNamesAsync(
        Guid? excludeId,
        CancellationToken ct)
    {
        return await db.AgentContexts
            .Where(c => excludeId == null || c.Id != excludeId)
            .Select(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ListChannelIdsForContextAsync(
        Guid contextId,
        CancellationToken ct)
    {
        return await db.Channels
            .Where(c => c.AgentContextId == contextId)
            .Select(c => c.Id)
            .ToListAsync(ct);
    }

    public void TrackChannel(ChannelDB channel)
    {
        db.Channels.Add(channel);
    }

    public void TrackContext(ChannelContextDB context)
    {
        db.AgentContexts.Add(context);
    }

    public void TrackThread(ChatThreadDB thread)
    {
        db.ChatThreads.Add(thread);
    }

    public void RemoveChannel(ChannelDB channel)
    {
        db.Channels.Remove(channel);
    }

    public void RemoveContext(ChannelContextDB context)
    {
        db.AgentContexts.Remove(context);
    }

    public void RemoveThread(ChatThreadDB thread)
    {
        db.ChatThreads.Remove(thread);
    }

    public async Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        buildInvalidationPlan?.Invoke()?.ApplyTo(chatCache);
    }

    private IQueryable<ChannelDB> ChannelsWithDetails()
    {
        return db.Channels
            .Include(c => c.Agent).ThenInclude(a => a!.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent).ThenInclude(a => a!.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Role)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Role);
    }

    private IQueryable<ChannelContextDB> ContextsWithDetails()
    {
        return db.AgentContexts
            .Include(c => c.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent).ThenInclude(a => a.Role)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Role);
    }
}
