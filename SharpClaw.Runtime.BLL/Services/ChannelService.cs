using SharpClaw.Core.Conversation;
using SharpClaw.Contracts.DTOs.Channels;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class ChannelService(
    ConversationAdministrationEngine administration,
    EfConversationAdministrationHost administrationHost)
{
    public async Task<ChannelResponse> CreateAsync(
        CreateChannelRequest request, CancellationToken ct = default)
    {
        return await administration.CreateChannelAsync(
            request,
            administrationHost,
            ct);
    }

    public async Task<ChannelResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        return await administration.GetChannelAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<ChannelResponse>> ListAsync(
        Guid? agentId = null, Guid? contextId = null, CancellationToken ct = default)
    {
        return await administration.ListChannelsAsync(
            agentId,
            contextId,
            administrationHost,
            ct);
    }

    public async Task<ChannelResponse?> UpdateAsync(
        Guid id, UpdateChannelRequest request, CancellationToken ct = default)
    {
        return await administration.UpdateChannelAsync(
            id,
            request,
            administrationHost,
            ct);
    }

    public async Task<ChannelResponse?> GetLatestActiveAsync(
        CancellationToken ct = default)
    {
        return await administration.GetLatestActiveChannelAsync(
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        return await administration.DeleteChannelAsync(
            id,
            administrationHost,
            ct);
    }

    public async Task<ChannelResponse?> SetAgentAsync(
        Guid channelId, Guid agentId, CancellationToken ct = default)
    {
        return await administration.SetChannelAgentAsync(
            channelId,
            agentId,
            administrationHost,
            ct);
    }

    public async Task<ChannelAllowedAgentsResponse?> ListAllowedAgentsAsync(
        Guid channelId, CancellationToken ct = default)
    {
        return await administration.GetChannelAllowedAgentsAsync(
            channelId,
            administrationHost,
            ct);
    }

    public async Task<ChannelAllowedAgentsResponse?> AddAllowedAgentAsync(
        Guid channelId, Guid agentId, CancellationToken ct = default)
    {
        return await administration.AddChannelAllowedAgentAsync(
            channelId,
            agentId,
            administrationHost,
            ct);
    }

    public async Task<ChannelAllowedAgentsResponse?> RemoveAllowedAgentAsync(
        Guid channelId, Guid agentId, CancellationToken ct = default)
    {
        return await administration.RemoveChannelAllowedAgentAsync(
            channelId,
            agentId,
            administrationHost,
            ct);
    }
}
