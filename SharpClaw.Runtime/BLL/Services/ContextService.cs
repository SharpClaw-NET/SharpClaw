using SharpClaw.Core.Conversation;
using SharpClaw.Contracts.DTOs.Contexts;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class ContextService(
    ConversationAdministrationEngine administration,
    EfConversationAdministrationHost administrationHost)
{
    public async Task<ContextResponse> CreateAsync(
        CreateContextRequest request, CancellationToken ct = default)
    {
        return await administration.CreateContextAsync(
            request,
            administrationHost,
            ct);
    }

    public async Task<ContextResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        return await administration.GetContextAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<ContextResponse>> ListAsync(
        Guid? agentId = null, CancellationToken ct = default)
    {
        return await administration.ListContextsAsync(
            agentId,
            administrationHost,
            ct);
    }

    public async Task<ContextResponse?> UpdateAsync(
        Guid id, UpdateContextRequest request, CancellationToken ct = default)
    {
        return await administration.UpdateContextAsync(
            id,
            request,
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        return await administration.DeleteContextAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<ContextAllowedAgentsResponse?> ListAllowedAgentsAsync(
        Guid contextId, CancellationToken ct = default)
    {
        return await administration.GetContextAllowedAgentsAsync(
            contextId,
            administrationHost,
            ct);
    }

    public async Task<ContextAllowedAgentsResponse?> AddAllowedAgentAsync(
        Guid contextId, Guid agentId, CancellationToken ct = default)
    {
        return await administration.AddContextAllowedAgentAsync(
            contextId,
            agentId,
            administrationHost,
            ct);
    }

    public async Task<ContextAllowedAgentsResponse?> RemoveAllowedAgentAsync(
        Guid contextId, Guid agentId, CancellationToken ct = default)
    {
        return await administration.RemoveContextAllowedAgentAsync(
            contextId,
            agentId,
            administrationHost,
            ct);
    }
}
