using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Core.Conversation;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ContextService(
    SharpClawDbContext db,
    IConfiguration configuration,
    ConversationTopologyEngine conversation,
    ChatCache chatCache)
{
    public async Task<ContextResponse> CreateAsync(
        CreateContextRequest request, CancellationToken ct = default)
    {
        if (request.Name is not null && IsUniqueContextNamesEnforced())
            await EnsureContextNameUniqueAsync(request.Name, excludeId: null, ct);

        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == request.AgentId, ct)
            ?? throw new ArgumentException($"Agent {request.AgentId} not found.");

        IReadOnlyList<AgentDB>? allowed = null;

        if (request.AllowedAgentIds is { Count: > 0 } agentIds)
        {
            allowed = await db.Agents
                .Include(a => a.Model).ThenInclude(m => m.Provider)
                .Include(a => a.Role)
                .Where(a => agentIds.Contains(a.Id))
                .ToListAsync(ct);
        }

        var context = conversation.CreateContext(
            request,
            agent,
            allowed,
            DateTimeOffset.UtcNow);

        db.AgentContexts.Add(context);
        await db.SaveChangesAsync(ct);

        return conversation.ToContextResponse(context);
    }

    public async Task<ContextResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(id, ct);
        return context is null ? null : conversation.ToContextResponse(context);
    }

    public async Task<IReadOnlyList<ContextResponse>> ListAsync(
        Guid? agentId = null, CancellationToken ct = default)
    {
        var query = db.AgentContexts
            .Include(c => c.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent).ThenInclude(a => a.Role)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Role)
            .AsQueryable();

        if (agentId is not null)
            query = query.Where(c => c.AgentId == agentId);

        var contexts = await query
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return contexts.Select(conversation.ToContextResponse).ToList();
    }

    public async Task<ContextResponse?> UpdateAsync(
        Guid id, UpdateContextRequest request, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(id, ct);
        if (context is null) return null;

        if (request.Name is not null)
        {
            if (IsUniqueContextNamesEnforced() && !request.Name.Trim().Equals(context.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                await EnsureContextNameUniqueAsync(request.Name, excludeId: id, ct);
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

        conversation.ApplyContextUpdate(context, request, allowed);

        await db.SaveChangesAsync(ct);
        await InvalidateContextRuntimeStateAsync(id, ct);
        return conversation.ToContextResponse(context);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var context = await db.AgentContexts.FindAsync([id], ct);
        if (context is null) return false;

        var channelIds = await db.Channels
            .Where(c => c.AgentContextId == id)
            .Select(c => c.Id)
            .ToListAsync(ct);

        db.AgentContexts.Remove(context);
        await db.SaveChangesAsync(ct);
        InvalidateContextChannelRuntimeState(channelIds);
        return true;
    }

    private bool IsUniqueContextNamesEnforced()
    {
        return ConversationTopologyEngine.IsUniqueNameEnforced(
            configuration["UniqueNames:Contexts"]);
    }

    private async Task EnsureContextNameUniqueAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var names = await db.AgentContexts
            .Where(c => excludeId == null || c.Id != excludeId)
            .Select(c => c.Name)
            .ToListAsync(ct);
        conversation.EnsureContextNameAvailable(name, names);
    }

    // ═══════════════════════════════════════════════════════════════
    // Granular operations
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Lists the allowed agents for a context.
    /// </summary>
    public async Task<ContextAllowedAgentsResponse?> ListAllowedAgentsAsync(
        Guid contextId, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(contextId, ct);
        if (context is null) return null;

        return conversation.ToContextAllowedAgentsResponse(context);
    }

    /// <summary>
    /// Adds an agent to the context's allowed agents.
    /// </summary>
    public async Task<ContextAllowedAgentsResponse?> AddAllowedAgentAsync(
        Guid contextId, Guid agentId, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(contextId, ct);
        if (context is null) return null;

        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new ArgumentException($"Agent {agentId} not found.");

        if (conversation.AddContextAllowedAgent(context, agent))
        {
            await db.SaveChangesAsync(ct);
            await InvalidateContextRuntimeStateAsync(contextId, ct);
        }

        return conversation.ToContextAllowedAgentsResponse(context);
    }

    /// <summary>
    /// Removes an agent from the context's allowed agents.
    /// </summary>
    public async Task<ContextAllowedAgentsResponse?> RemoveAllowedAgentAsync(
        Guid contextId, Guid agentId, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(contextId, ct);
        if (context is null) return null;

        if (conversation.RemoveContextAllowedAgent(context, agentId))
        {
            await db.SaveChangesAsync(ct);
            await InvalidateContextRuntimeStateAsync(contextId, ct);
        }

        return conversation.ToContextAllowedAgentsResponse(context);
    }

    private async Task<ChannelContextDB?> LoadContextAsync(Guid id, CancellationToken ct) =>
        await db.AgentContexts
            .Include(c => c.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent).ThenInclude(a => a.Role)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Role)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    private async Task InvalidateContextRuntimeStateAsync(Guid contextId, CancellationToken ct)
    {
        var channelIds = await db.Channels
            .Where(c => c.AgentContextId == contextId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        InvalidateContextChannelRuntimeState(channelIds);
    }

    private void InvalidateContextChannelRuntimeState(IEnumerable<Guid> channelIds)
    {
        foreach (var channelId in channelIds)
        {
            chatCache.RemoveHeaderAgentSuffixesForChannel(channelId);
            chatCache.RemoveDefaultResourceResolutionForChannel(channelId);
        }
    }

}
