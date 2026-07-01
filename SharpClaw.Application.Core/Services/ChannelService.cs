using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Core.Conversation;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ChannelService(
    SharpClawDbContext db,
    IConfiguration configuration,
    ConversationTopologyEngine conversation,
    ChatRuntimeInvalidationPlanner invalidations,
    ChatCache chatCache)
{
    /// <summary>
    /// Creates a new channel.  Either <see cref="CreateChannelRequest.AgentId"/>
    /// or <see cref="CreateChannelRequest.ContextId"/> (whose context has an
    /// agent) must be provided so the channel has a resolvable agent.
    /// </summary>
    public async Task<ChannelResponse> CreateAsync(
        CreateChannelRequest request, CancellationToken ct = default)
    {
        AgentDB? agent = null;
        if (request.AgentId is { } agentId)
        {
            agent = await db.Agents
                .Include(a => a.Model).ThenInclude(m => m.Provider)
                .Include(a => a.Role)
                .FirstOrDefaultAsync(a => a.Id == agentId, ct)
                ?? throw new ArgumentException($"Agent {agentId} not found.");
        }

        ChannelContextDB? context = null;
        if (request.ContextId is { } ctxId)
        {
            context = await db.AgentContexts
                .Include(c => c.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
                .Include(c => c.Agent).ThenInclude(a => a.Role)
                .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
                .Include(c => c.AllowedAgents).ThenInclude(a => a.Role)
                .FirstOrDefaultAsync(c => c.Id == ctxId, ct)
                ?? throw new ArgumentException($"Context {ctxId} not found.");
        }

        var now = DateTimeOffset.UtcNow;
        var title = request.Title
            ?? ConversationTopologyEngine.BuildDefaultChannelTitle(now);

        if (IsUniqueChannelNamesEnforced())
            await EnsureChannelTitleUniqueAsync(title, excludeId: null, ct);

        IReadOnlyList<AgentDB>? allowed = null;

        if (request.AllowedAgentIds is { Count: > 0 } agentIds)
        {
            allowed = await db.Agents
                .Include(a => a.Model).ThenInclude(m => m.Provider)
                .Include(a => a.Role)
                .Where(a => agentIds.Contains(a.Id))
                .ToListAsync(ct);
        }

        var channel = conversation.CreateChannel(
            request with { Title = title },
            agent,
            context,
            allowed,
            now);

        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct);

        return conversation.ToChannelResponse(channel);
    }

    public async Task<ChannelResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var channel = await LoadChannelAsync(id, ct);
        if (channel is null) return null;

        return conversation.ToChannelResponse(channel);
    }

    /// <summary>
    /// Lists channels, optionally filtered by agent or context.
    /// </summary>
    public async Task<IReadOnlyList<ChannelResponse>> ListAsync(
        Guid? agentId = null, Guid? contextId = null, CancellationToken ct = default)
    {
        var query = db.Channels
            .Include(c => c.Agent).ThenInclude(a => a!.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent).ThenInclude(a => a!.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Role)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Role)
            .AsQueryable();

        if (agentId is not null)
            query = query.Where(c => c.AgentId == agentId);

        if (contextId is not null)
            query = query.Where(c => c.AgentContextId == contextId);

        var channels = await query
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return channels
            .Select(conversation.ToChannelResponse)
            .ToList();
    }

    public async Task<ChannelResponse?> UpdateAsync(
        Guid id, UpdateChannelRequest request, CancellationToken ct = default)
    {
        var channel = await LoadChannelAsync(id, ct);
        if (channel is null) return null;

        if (request.Title is not null)
        {
            if (IsUniqueChannelNamesEnforced() && !request.Title.Trim().Equals(channel.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                await EnsureChannelTitleUniqueAsync(request.Title, excludeId: id, ct);
        }

        ChannelContextDB? context = null;
        if (request.ContextId is not null)
        {
            if (request.ContextId != Guid.Empty)
            {
                context = await db.AgentContexts
                    .FirstOrDefaultAsync(c => c.Id == request.ContextId, ct)
                    ?? throw new ArgumentException($"Context {request.ContextId} not found.");
            }
        }

        IReadOnlyList<AgentDB>? allowed = null;
        if (request.AllowedAgentIds is not null)
        {
            allowed = request.AllowedAgentIds.Count > 0
                ? await db.Agents
                    .Where(a => request.AllowedAgentIds.Contains(a.Id))
                    .ToListAsync(ct)
                : [];
        }

        conversation.ApplyChannelUpdate(channel, request, context, allowed);

        await db.SaveChangesAsync(ct);
        InvalidateChannelRuntimeState(id);
        return conversation.ToChannelResponse(channel);
    }

    /// <summary>
    /// Returns the channel with the most recent chat message across all
    /// agents.  Falls back to the most recently created channel when no
    /// messages exist.  Used by the CLI when no channel is explicitly
    /// selected.
    /// </summary>
    public async Task<ChannelResponse?> GetLatestActiveAsync(CancellationToken ct = default)
    {
        // Find the channel that received the most recent message.
        var latestChannelId = await db.ChatMessages
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => (Guid?)m.ChannelId)
            .FirstOrDefaultAsync(ct);

        ChannelDB? channel;
        if (latestChannelId is not null)
        {
            channel = await LoadChannelAsync(latestChannelId.Value, ct);
        }
        else
        {
            // No messages in any channel — fall back to most recently created.
            channel = await db.Channels
                .Include(c => c.Agent).ThenInclude(a => a!.Model).ThenInclude(m => m.Provider)
                .Include(c => c.Agent).ThenInclude(a => a!.Role)
                .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
                .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Role)
                .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
                .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Role)
                .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
                .Include(c => c.AllowedAgents).ThenInclude(a => a.Role)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
        }

        if (channel is null) return null;
        return conversation.ToChannelResponse(channel);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var channel = await db.Channels.FindAsync([id], ct);
        if (channel is null) return false;

        db.Channels.Remove(channel);
        await db.SaveChangesAsync(ct);
        InvalidateChannelRuntimeState(id);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Granular operations
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets the default agent for a channel.
    /// </summary>
    public async Task<ChannelResponse?> SetAgentAsync(
        Guid channelId, Guid agentId, CancellationToken ct = default)
    {
        var channel = await LoadChannelAsync(channelId, ct);
        if (channel is null) return null;

        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new ArgumentException($"Agent {agentId} not found.");

        conversation.SetChannelAgent(channel, agent);
        await db.SaveChangesAsync(ct);
        InvalidateChannelRuntimeState(channelId);
        return conversation.ToChannelResponse(channel);
    }

    /// <summary>
    /// Lists the allowed agents for a channel (effective: channel's own,
    /// falling back to context's).
    /// </summary>
    public async Task<ChannelAllowedAgentsResponse?> ListAllowedAgentsAsync(
        Guid channelId, CancellationToken ct = default)
    {
        var channel = await LoadChannelAsync(channelId, ct);
        if (channel is null) return null;

        return conversation.ToChannelAllowedAgentsResponse(channel);
    }

    /// <summary>
    /// Adds an agent to the channel's allowed agents.
    /// </summary>
    public async Task<ChannelAllowedAgentsResponse?> AddAllowedAgentAsync(
        Guid channelId, Guid agentId, CancellationToken ct = default)
    {
        var channel = await LoadChannelAsync(channelId, ct);
        if (channel is null) return null;

        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new ArgumentException($"Agent {agentId} not found.");

        if (conversation.AddChannelAllowedAgent(channel, agent))
        {
            await db.SaveChangesAsync(ct);
            InvalidateChannelRuntimeState(channelId);
        }

        return conversation.ToChannelAllowedAgentsResponse(channel);
    }

    /// <summary>
    /// Removes an agent from the channel's allowed agents.
    /// </summary>
    public async Task<ChannelAllowedAgentsResponse?> RemoveAllowedAgentAsync(
        Guid channelId, Guid agentId, CancellationToken ct = default)
    {
        var channel = await LoadChannelAsync(channelId, ct);
        if (channel is null) return null;

        if (conversation.RemoveChannelAllowedAgent(channel, agentId))
        {
            await db.SaveChangesAsync(ct);
            InvalidateChannelRuntimeState(channelId);
        }

        return conversation.ToChannelAllowedAgentsResponse(channel);
    }

    // ── Private helpers ───────────────────────────────────────────

    private async Task<ChannelDB?> LoadChannelAsync(Guid id, CancellationToken ct) =>
        await db.Channels
            .Include(c => c.Agent).ThenInclude(a => a!.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent).ThenInclude(a => a!.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent).ThenInclude(a => a.Role)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents).ThenInclude(a => a.Role)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Role)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    private void InvalidateChannelRuntimeState(Guid channelId)
    {
        invalidations.ChannelChanged(channelId).ApplyTo(chatCache);
    }

    private bool IsUniqueChannelNamesEnforced()
    {
        return ConversationTopologyEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Channels"]);
    }

    private async Task EnsureChannelTitleUniqueAsync(string title, Guid? excludeId, CancellationToken ct)
    {
        var titles = await db.Channels
            .Where(c => excludeId == null || c.Id != excludeId)
            .Select(c => c.Title)
            .ToListAsync(ct);
        conversation.EnsureChannelTitleAvailable(title, titles);
    }
}
