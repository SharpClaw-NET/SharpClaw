using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ContextService(SharpClawDbContext db)
{
    public async Task<ContextResponse> CreateAsync(
        CreateContextRequest request, CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == request.AgentId, ct)
            ?? throw new ArgumentException($"Agent {request.AgentId} not found.");

        var context = new ChannelContextDB
        {
            Name = request.Name ?? $"Context {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}",
            AgentId = agent.Id,
            PermissionSetId = request.PermissionSetId,
            DisableChatHeader = request.DisableChatHeader ?? false
        };

        if (request.AllowedAgentIds is { Count: > 0 } agentIds)
        {
            var allowed = await db.Agents
                .Include(a => a.Model).ThenInclude(m => m.Provider)
                .Include(a => a.Role)
                .Where(a => agentIds.Contains(a.Id))
                .ToListAsync(ct);
            foreach (var a in allowed)
                context.AllowedAgents.Add(a);
        }

        db.AgentContexts.Add(context);
        await db.SaveChangesAsync(ct);

        return ToResponse(context, agent);
    }

    public async Task<ContextResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(id, ct);
        return context is null ? null : ToResponse(context, context.Agent);
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

        return contexts.Select(c => ToResponse(c, c.Agent)).ToList();
    }

    public async Task<ContextResponse?> UpdateAsync(
        Guid id, UpdateContextRequest request, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(id, ct);
        if (context is null) return null;

        if (request.Name is not null)
            context.Name = request.Name;

        // Allow explicit set/unset of permission set
        if (request.PermissionSetId is not null)
            context.PermissionSetId = request.PermissionSetId == Guid.Empty
                ? null
                : request.PermissionSetId;

        if (request.DisableChatHeader is not null)
            context.DisableChatHeader = request.DisableChatHeader.Value;

        // Replace the allowed-agents set when provided.
        if (request.AllowedAgentIds is not null)
        {
            context.AllowedAgents.Clear();
            if (request.AllowedAgentIds.Count > 0)
            {
                var allowed = await db.Agents
                    .Where(a => request.AllowedAgentIds.Contains(a.Id))
                    .ToListAsync(ct);
                foreach (var a in allowed)
                    context.AllowedAgents.Add(a);
            }
        }

        await db.SaveChangesAsync(ct);
        return ToResponse(context, context.Agent);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var context = await db.AgentContexts.FindAsync([id], ct);
        if (context is null) return false;

        db.AgentContexts.Remove(context);
        await db.SaveChangesAsync(ct);
        return true;
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

        return new(contextId,
            ChannelService.ToSummary(context.Agent),
            context.AllowedAgents.Select(ChannelService.ToSummary).ToList());
    }

    /// <summary>
    /// Adds an agent to the context's allowed agents.
    /// </summary>
    public async Task<ContextAllowedAgentsResponse?> AddAllowedAgentAsync(
        Guid contextId, Guid agentId, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(contextId, ct);
        if (context is null) return null;

        if (context.AllowedAgents.Any(a => a.Id == agentId))
            return await ListAllowedAgentsAsync(contextId, ct);

        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct)
            ?? throw new ArgumentException($"Agent {agentId} not found.");

        context.AllowedAgents.Add(agent);
        await db.SaveChangesAsync(ct);
        return await ListAllowedAgentsAsync(contextId, ct);
    }

    /// <summary>
    /// Removes an agent from the context's allowed agents.
    /// </summary>
    public async Task<ContextAllowedAgentsResponse?> RemoveAllowedAgentAsync(
        Guid contextId, Guid agentId, CancellationToken ct = default)
    {
        var context = await LoadContextAsync(contextId, ct);
        if (context is null) return null;

        var agent = context.AllowedAgents.FirstOrDefault(a => a.Id == agentId);
        if (agent is not null)
        {
            context.AllowedAgents.Remove(agent);
            await db.SaveChangesAsync(ct);
        }

        return await ListAllowedAgentsAsync(contextId, ct);
    }

    private async Task<ChannelContextDB?> LoadContextAsync(Guid id, CancellationToken ct) =>
        await db.AgentContexts
            .Include(c => c.Agent).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent).ThenInclude(a => a.Role)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Model).ThenInclude(m => m.Provider)
            .Include(c => c.AllowedAgents).ThenInclude(a => a.Role)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    private static ContextResponse ToResponse(ChannelContextDB context, AgentDB agent) =>
        new(context.Id,
            context.Name,
            ChannelService.ToSummary(agent),
            context.PermissionSetId,
            context.DisableChatHeader,
            context.AllowedAgents.Select(ChannelService.ToSummary).ToList(),
            context.CreatedAt,
            context.UpdatedAt);
}
