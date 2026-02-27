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
            .Include(c => c.Agent)
            .Include(c => c.AllowedAgents)
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

    private async Task<ChannelContextDB?> LoadContextAsync(Guid id, CancellationToken ct) =>
        await db.AgentContexts
            .Include(c => c.Agent)
            .Include(c => c.AllowedAgents)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    private static ContextResponse ToResponse(ChannelContextDB context, AgentDB agent) =>
        new(context.Id,
            context.Name,
            agent.Id,
            agent.Name,
            context.PermissionSetId,
            context.DisableChatHeader,
            context.AllowedAgents.Select(a => a.Id).ToList(),
            context.CreatedAt,
            context.UpdatedAt);
}
