using SharpClaw.Core.Conversation;
using SharpClaw.Contracts.DTOs.Threads;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class ThreadService(
    ConversationAdministrationEngine administration,
    EfConversationAdministrationHost administrationHost)
{
    public async Task<ThreadResponse> CreateAsync(
        Guid channelId, CreateThreadRequest request, CancellationToken ct = default)
    {
        return await administration.CreateThreadAsync(
            channelId,
            request,
            administrationHost,
            ct);
    }

    public async Task<ThreadResponse?> GetByIdAsync(
        Guid threadId, CancellationToken ct = default)
    {
        return await administration.GetThreadAsync(
            threadId,
            administrationHost,
            ct);
    }

    public async Task<IReadOnlyList<ThreadResponse>> ListAsync(
        Guid channelId, CancellationToken ct = default)
    {
        return await administration.ListThreadsAsync(
            channelId,
            administrationHost,
            ct);
    }

    public async Task<ThreadResponse?> UpdateAsync(
        Guid threadId, UpdateThreadRequest request, CancellationToken ct = default)
    {
        return await administration.UpdateThreadAsync(
            threadId,
            request,
            administrationHost,
            ct);
    }

    public async Task<bool> DeleteAsync(Guid threadId, CancellationToken ct = default)
    {
        return await administration.DeleteThreadAsync(
            threadId,
            administrationHost,
            ct);
    }
}
