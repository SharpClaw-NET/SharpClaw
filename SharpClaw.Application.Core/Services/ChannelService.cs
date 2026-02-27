using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ChannelService(SharpClawDbContext db)
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
                .FirstOrDefaultAsync(a => a.Id == agentId, ct)
                ?? throw new ArgumentException($"Agent {agentId} not found.");
        }

        ChannelContextDB? context = null;
        if (request.ContextId is { } ctxId)
        {
            context = await db.AgentContexts
                .Include(c => c.Agent)
                .FirstOrDefaultAsync(c => c.Id == ctxId, ct)
                ?? throw new ArgumentException($"Context {ctxId} not found.");
        }

        // At least one source of agent is required.
        if (agent is null && context is null)
            throw new ArgumentException(
                "Either an AgentId or a ContextId (with an agent) is required.");

        var channel = new ChannelDB
        {
            Title = request.Title ?? $"Channel {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}",
            AgentId = agent?.Id,
            AgentContextId = context?.Id,
            PermissionSetId = request.PermissionSetId,
            DisableChatHeader = request.DisableChatHeader ?? false
        };

        if (request.AllowedAgentIds is { Count: > 0 } agentIds)
        {
            var allowed = await db.Agents
                .Where(a => agentIds.Contains(a.Id))
                .ToListAsync(ct);
            foreach (var a in allowed)
                channel.AllowedAgents.Add(a);
        }

        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct);

        return ToResponse(channel, agent, context);
    }

    public async Task<ChannelResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var channel = await LoadChannelAsync(id, ct);
        if (channel is null) return null;

        return ToResponse(channel, channel.Agent, channel.AgentContext);
    }

    /// <summary>
    /// Lists channels, optionally filtered by agent or context.
    /// </summary>
    public async Task<IReadOnlyList<ChannelResponse>> ListAsync(
        Guid? agentId = null, Guid? contextId = null, CancellationToken ct = default)
    {
        var query = db.Channels
            .Include(c => c.Agent)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents)
            .Include(c => c.AllowedAgents)
            .AsQueryable();

        if (agentId is not null)
            query = query.Where(c => c.AgentId == agentId);

        if (contextId is not null)
            query = query.Where(c => c.AgentContextId == contextId);

        var channels = await query
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return channels
            .Select(c => ToResponse(c, c.Agent, c.AgentContext))
            .ToList();
    }

    public async Task<ChannelResponse?> UpdateAsync(
        Guid id, UpdateChannelRequest request, CancellationToken ct = default)
    {
        var channel = await LoadChannelAsync(id, ct);
        if (channel is null) return null;

        if (request.Title is not null)
            channel.Title = request.Title;

        // Allow moving into / out of a context
        if (request.ContextId is not null)
        {
            if (request.ContextId == Guid.Empty)
            {
                channel.AgentContextId = null;
                channel.AgentContext = null;
            }
            else
            {
                var ctx = await db.AgentContexts
                    .FirstOrDefaultAsync(c => c.Id == request.ContextId, ct)
                    ?? throw new ArgumentException($"Context {request.ContextId} not found.");
                channel.AgentContextId = ctx.Id;
                channel.AgentContext = ctx;
            }
        }

        // Allow explicit set/unset of permission set
        if (request.PermissionSetId is not null)
            channel.PermissionSetId = request.PermissionSetId == Guid.Empty
                ? null
                : request.PermissionSetId;

        // Replace the allowed-agents set when provided.
        if (request.AllowedAgentIds is not null)
        {
            channel.AllowedAgents.Clear();
            if (request.AllowedAgentIds.Count > 0)
            {
                var allowed = await db.Agents
                    .Where(a => request.AllowedAgentIds.Contains(a.Id))
                    .ToListAsync(ct);
                foreach (var a in allowed)
                    channel.AllowedAgents.Add(a);
            }
        }

        if (request.DisableChatHeader is not null)
            channel.DisableChatHeader = request.DisableChatHeader.Value;

        await db.SaveChangesAsync(ct);
        return ToResponse(channel, channel.Agent, channel.AgentContext);
    }

    /// <summary>
    /// Returns the channel with the most recent chat message across all
    /// agents.  Used by the CLI when no channel is explicitly selected.
    /// </summary>
    public async Task<ChannelResponse?> GetLatestActiveAsync(CancellationToken ct = default)
    {
        var channel = await db.Channels
            .Include(c => c.Agent)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents)
            .Include(c => c.AllowedAgents)
            .Include(c => c.ChatMessages)
            .OrderByDescending(c => c.ChatMessages.Max(m => (DateTimeOffset?)m.CreatedAt) ?? c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (channel is null) return null;
        return ToResponse(channel, channel.Agent, channel.AgentContext);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var channel = await db.Channels.FindAsync([id], ct);
        if (channel is null) return false;

        db.Channels.Remove(channel);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────

    private async Task<ChannelDB?> LoadChannelAsync(Guid id, CancellationToken ct) =>
        await db.Channels
            .Include(c => c.Agent)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.Agent)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents)
            .Include(c => c.AllowedAgents)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    private static ChannelResponse ToResponse(
        ChannelDB channel, AgentDB? agent, ChannelContextDB? context)
    {
        // Effective agent: channel's own agent, or the context's agent.
        var effectiveAgent = agent ?? context?.Agent;

        // Effective allowed agents: channel's own, falling back to context's.
        var effectiveAllowed = channel.AllowedAgents.Count > 0
            ? channel.AllowedAgents.Select(a => a.Id).ToList()
            : context?.AllowedAgents.Select(a => a.Id).ToList() ?? [];

        return new(channel.Id,
            channel.Title,
            effectiveAgent?.Id,
            effectiveAgent?.Name,
            context?.Id,
            context?.Name,
            channel.PermissionSetId,
            channel.PermissionSetId ?? context?.PermissionSetId,
            effectiveAllowed,
            channel.DisableChatHeader,
            channel.CreatedAt,
            channel.UpdatedAt);
    }
}
