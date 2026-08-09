using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Chat;
using SharpClaw.Core.State;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

public sealed class EfChatQueryHost(
    SharpClawDbContext db,
    IPersistenceEntityResolver entities,
    ChatCache chatCache) : IChatQueryHost
{
    private readonly CoreStateSession _states = new(db);

    public async Task<IReadOnlyList<ChatMessageState>> ListHistoryMessagesAsync(
        Guid channelId,
        Guid? threadId,
        int limit,
        CancellationToken ct)
    {
        var hint = threadId is not null
            ? new PersistenceQueryHint("ThreadId", threadId.Value)
            : new PersistenceQueryHint("ChannelId", channelId);
        var hasThread = threadId is not null;

        var records = await entities.QueryAsync<ChatMessageDB>(
            db,
            message => hasThread
                ? message.ThreadId == threadId
                : message.ChannelId == channelId,
            limit,
            hint,
            ct);
        return _states.Map(records);
    }

    public async Task<ChatThreadHistoryLimitValues?> LoadThreadHistoryLimitValuesAsync(
        Guid threadId,
        CancellationToken ct)
    {
        var limits = await db.ChatThreads
            .AsNoTracking()
            .Where(thread => thread.Id == threadId)
            .Select(thread => new
            {
                thread.MaxMessages,
                thread.MaxCharacters
            })
            .FirstOrDefaultAsync(ct);

        return limits is null
            ? null
            : new ChatThreadHistoryLimitValues(
                limits.MaxMessages,
                limits.MaxCharacters);
    }

    public async Task<ChatHistoryLimits?> GetOrCreateThreadHistoryLimitsAsync(
        Guid threadId,
        Func<CancellationToken, Task<ChatHistoryLimits>> loader,
        CancellationToken ct) =>
        await chatCache.GetOrCreateAsync(
            ChatCache.KeyThreadHistoryLimits(threadId),
            async innerCt => await loader(innerCt),
            static _ => 16,
            ct);

    public async Task<IReadOnlyList<ChatMessageState>> ListThreadHistoryMessagesAsync(
        Guid threadId,
        int limit,
        CancellationToken ct) =>
        _states.Map(await entities.QueryAsync<ChatMessageDB>(
            db,
            message => message.ThreadId == threadId,
            limit,
            new PersistenceQueryHint("ThreadId", threadId),
            ct));

    public async Task<ChannelCostResponse> GetOrCreateChannelCostAsync(
        Guid channelId,
        Func<CancellationToken, Task<ChannelCostResponse>> loader,
        CancellationToken ct) =>
        await chatCache.GetChannelCostAsync(channelId, loader, ct);

    public async Task<ThreadCostResponse?> GetOrCreateThreadCostAsync(
        Guid channelId,
        Guid threadId,
        Func<CancellationToken, Task<ThreadCostResponse?>> loader,
        CancellationToken ct) =>
        await chatCache.GetThreadCostAsync(channelId, threadId, loader, ct);

    public async Task<AgentCostResponse?> GetOrCreateAgentCostAsync(
        Guid agentId,
        Func<CancellationToken, Task<AgentCostResponse?>> loader,
        CancellationToken ct) =>
        await chatCache.GetAgentCostAsync(agentId, loader, ct);

    public async Task<IReadOnlyList<ChatMessageState>> ListChannelCostMessagesAsync(
        Guid channelId,
        CancellationToken ct) =>
        _states.Map(await entities.QueryAsync<ChatMessageDB>(
            db,
            message => message.ChannelId == channelId
                && message.PromptTokens != null,
            hint: new PersistenceQueryHint("ChannelId", channelId),
            ct: ct));

    public async Task<bool> ThreadBelongsToChannelAsync(
        Guid channelId,
        Guid threadId,
        CancellationToken ct) =>
        await db.ChatThreads.AnyAsync(
            thread => thread.Id == threadId && thread.ChannelId == channelId,
            ct);

    public async Task<IReadOnlyList<ChatMessageState>> ListThreadCostMessagesAsync(
        Guid threadId,
        CancellationToken ct) =>
        _states.Map(await entities.QueryAsync<ChatMessageDB>(
            db,
            message => message.ThreadId == threadId
                && message.PromptTokens != null,
            hint: new PersistenceQueryHint("ThreadId", threadId),
            ct: ct));

    public async Task<string?> LoadAgentNameAsync(
        Guid agentId,
        CancellationToken ct) =>
        await db.Agents
            .Where(agent => agent.Id == agentId)
            .Select(agent => agent.Name)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ChatMessageState>> ListAgentCostMessagesAsync(
        Guid agentId,
        CancellationToken ct) =>
        _states.Map(await entities.QueryAsync<ChatMessageDB>(
            db,
            message => message.SenderAgentId == agentId
                && message.PromptTokens != null,
            hint: new PersistenceQueryHint("SenderAgentId", agentId),
            ct: ct));
}
