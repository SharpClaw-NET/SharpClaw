using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Core.Conversation;
using SharpClaw.Core.State;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfConversationAdministrationHost(
    SharpClawDbContext db,
    IConfiguration configuration,
    ChatCache chatCache) : IConversationAdministrationHost
{
    private readonly CoreStateSession _states = new(db);

    public bool UniqueChannelNamesEnforced =>
        ConversationTopologyEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Channels"]);

    public bool UniqueContextNamesEnforced =>
        ConversationTopologyEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Contexts"]);

    public async Task<AgentState?> LoadAgentAsync(
        Guid agentId,
        CancellationToken ct)
    {
        var entity = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<IReadOnlyList<AgentState>> LoadAgentsAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken ct)
    {
        var entities = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .Where(a => agentIds.Contains(a.Id))
            .ToListAsync(ct);
        return _states.Map(entities);
    }

    public async Task<ChannelState?> LoadChannelAsync(
        Guid channelId,
        CancellationToken ct)
    {
        var entity = await ChannelsWithDetails()
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<ChannelContextState?> LoadContextAsync(
        Guid contextId,
        CancellationToken ct)
    {
        var entity = await ContextsWithDetails()
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<ChatThreadState?> LoadThreadAsync(
        Guid threadId,
        CancellationToken ct)
    {
        var entity = await db.ChatThreads.FindAsync([threadId], ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<bool> ChannelExistsAsync(
        Guid channelId,
        CancellationToken ct)
    {
        return await db.Channels.AnyAsync(c => c.Id == channelId, ct);
    }

    public async Task<IReadOnlyList<ChannelState>> ListChannelsAsync(
        Guid? agentId,
        Guid? contextId,
        CancellationToken ct)
    {
        var query = ChannelsWithDetails();

        if (agentId is not null)
            query = query.Where(c => c.AgentId == agentId);

        if (contextId is not null)
            query = query.Where(c => c.AgentContextId == contextId);

        return _states.Map(await query.ToListAsync(ct));
    }

    public async Task<Guid?> LoadLatestMessageChannelIdAsync(CancellationToken ct)
    {
        return await db.ChatMessages
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => (Guid?)m.ChannelId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ChannelState?> LoadMostRecentlyCreatedChannelAsync(
        CancellationToken ct)
    {
        var entity = await ChannelsWithDetails()
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : _states.Map(entity);
    }

    public async Task<IReadOnlyList<ChannelContextState>> ListContextsAsync(
        Guid? agentId,
        CancellationToken ct)
    {
        var query = ContextsWithDetails();

        if (agentId is not null)
            query = query.Where(c => c.AgentId == agentId);

        return _states.Map(await query.ToListAsync(ct));
    }

    public async Task<IReadOnlyList<ChatThreadState>> ListThreadsAsync(
        Guid channelId,
        CancellationToken ct)
    {
        var entities = await db.ChatThreads
            .Where(t => t.ChannelId == channelId)
            .ToListAsync(ct);
        return _states.Map(entities);
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

    public void TrackChannel(ChannelState channel)
    {
        _states.Track(channel);
    }

    public void TrackContext(ChannelContextState context)
    {
        _states.Track(context);
    }

    public void TrackThread(ChatThreadState thread)
    {
        _states.Track(thread);
    }

    public void RemoveChannel(ChannelState channel)
    {
        _states.Remove(channel);
    }

    public void RemoveContext(ChannelContextState context)
    {
        _states.Remove(context);
    }

    public void RemoveThread(ChatThreadState thread)
    {
        _states.Remove(thread);
    }

    public async Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct)
    {
        await _states.SaveChangesAsync(ct);
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
