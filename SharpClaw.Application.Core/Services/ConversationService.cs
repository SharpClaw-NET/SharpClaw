using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Conversation;
using SharpClaw.Contracts.DTOs.Conversations;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ConversationService(SharpClawDbContext db)
{
    /// <summary>
    /// Creates a new conversation for the given agent.  If no model is
    /// specified the agent's current model is used.  An optional
    /// <see cref="AgentContextDB"/> can be linked so that the context's
    /// permission set acts as a default.
    /// </summary>
    public async Task<ConversationResponse> CreateAsync(
        CreateConversationRequest request, CancellationToken ct = default)
    {
        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .FirstOrDefaultAsync(a => a.Id == request.AgentId, ct)
            ?? throw new ArgumentException($"Agent {request.AgentId} not found.");

        var modelId = request.ModelId ?? agent.ModelId;
        var model = modelId == agent.ModelId
            ? agent.Model
            : await db.Models.Include(m => m.Provider)
                  .FirstOrDefaultAsync(m => m.Id == modelId, ct)
              ?? throw new ArgumentException($"Model {modelId} not found.");

        AgentContextDB? context = null;
        if (request.ContextId is { } ctxId)
        {
            context = await db.AgentContexts
                .FirstOrDefaultAsync(c => c.Id == ctxId, ct)
                ?? throw new ArgumentException($"Context {ctxId} not found.");
        }

        var conversation = new ConversationDB
        {
            Title = request.Title ?? $"Conversation {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}",
            ModelId = model.Id,
            AgentId = agent.Id,
            AgentContextId = context?.Id,
            PermissionSetId = request.PermissionSetId
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);

        return ToResponse(conversation, agent, model, context);
    }

    public async Task<ConversationResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return null;

        return ToResponse(conversation, conversation.Agent, conversation.Model, conversation.AgentContext);
    }

    /// <summary>
    /// Lists conversations, optionally filtered by agent or context.
    /// </summary>
    public async Task<IReadOnlyList<ConversationResponse>> ListAsync(
        Guid? agentId = null, Guid? contextId = null, CancellationToken ct = default)
    {
        var query = db.Conversations
            .Include(c => c.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent)
            .Include(c => c.AgentContext)
            .AsQueryable();

        if (agentId is not null)
            query = query.Where(c => c.AgentId == agentId);

        if (contextId is not null)
            query = query.Where(c => c.AgentContextId == contextId);

        var conversations = await query
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return conversations
            .Select(c => ToResponse(c, c.Agent, c.Model, c.AgentContext))
            .ToList();
    }

    public async Task<ConversationResponse?> UpdateAsync(
        Guid id, UpdateConversationRequest request, CancellationToken ct = default)
    {
        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return null;

        if (request.Title is not null)
            conversation.Title = request.Title;

        if (request.ModelId is { } modelId)
        {
            var model = await db.Models.Include(m => m.Provider)
                .FirstOrDefaultAsync(m => m.Id == modelId, ct)
                ?? throw new ArgumentException($"Model {modelId} not found.");
            conversation.ModelId = model.Id;
            conversation.Model = model;
        }

        // Allow moving into / out of a context
        if (request.ContextId is not null)
        {
            if (request.ContextId == Guid.Empty)
            {
                conversation.AgentContextId = null;
                conversation.AgentContext = null;
            }
            else
            {
                var ctx = await db.AgentContexts
                    .FirstOrDefaultAsync(c => c.Id == request.ContextId, ct)
                    ?? throw new ArgumentException($"Context {request.ContextId} not found.");
                conversation.AgentContextId = ctx.Id;
                conversation.AgentContext = ctx;
            }
        }

        // Allow explicit set/unset of permission set
        if (request.PermissionSetId is not null)
            conversation.PermissionSetId = request.PermissionSetId == Guid.Empty
                ? null
                : request.PermissionSetId;

        await db.SaveChangesAsync(ct);
        return ToResponse(conversation, conversation.Agent, conversation.Model, conversation.AgentContext);
    }

    /// <summary>
    /// Returns the conversation with the most recent chat message across all
    /// agents.  Used by the CLI when no conversation is explicitly selected.
    /// </summary>
    public async Task<ConversationResponse?> GetLatestActiveAsync(CancellationToken ct = default)
    {
        var conversation = await db.Conversations
            .Include(c => c.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent)
            .Include(c => c.AgentContext)
            .Include(c => c.ChatMessages)
            .OrderByDescending(c => c.ChatMessages.Max(m => (DateTimeOffset?)m.CreatedAt) ?? c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (conversation is null) return null;
        return ToResponse(conversation, conversation.Agent, conversation.Model, conversation.AgentContext);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var conversation = await db.Conversations.FindAsync([id], ct);
        if (conversation is null) return false;

        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────

    private async Task<ConversationDB?> LoadConversationAsync(Guid id, CancellationToken ct) =>
        await db.Conversations
            .Include(c => c.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent)
            .Include(c => c.AgentContext)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    private static ConversationResponse ToResponse(
        ConversationDB conversation, AgentDB agent, ModelDB model, AgentContextDB? context) =>
        new(conversation.Id,
            conversation.Title,
            agent.Id,
            agent.Name,
            model.Id,
            model.Name,
            context?.Id,
            context?.Name,
            conversation.PermissionSetId,
            conversation.PermissionSetId ?? context?.PermissionSetId,
            conversation.CreatedAt,
            conversation.UpdatedAt);
}
