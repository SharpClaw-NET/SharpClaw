using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Persists direct-chat history through the existing Runtime EF model.</summary>
public sealed class EfConversationStore(IServiceScopeFactory scopeFactory) : IConversationStore
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _conversationLocks = [];

    public async ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        CancellationToken ct)
    {
        if (conversationId == Guid.Empty)
            throw new ArgumentException("The conversation identifier must not be empty.", nameof(conversationId));
        ct.ThrowIfCancellationRequested();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var messages = await db.ChatMessages
            .AsNoTracking()
            .Where(message => message.ChannelId == conversationId && message.ThreadId == null)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .ToListAsync(ct);

        return messages
            .Select(message => new ChatCompletionMessage(message.Role, message.Content)
            {
                ProviderMetadataJson = message.ProviderMetadataJson,
            })
            .ToArray();
    }

    public async ValueTask CommitExchangeAsync(ChatExchange exchange, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        var conversationId = exchange.Turn.Conversation.ConversationId;
        if (conversationId == Guid.Empty)
            throw new ArgumentException("The conversation identifier must not be empty.", nameof(exchange));

        var gate = _conversationLocks.GetOrAdd(conversationId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var channelExists = await db.Channels
                .AsNoTracking()
                .AnyAsync(channel => channel.Id == conversationId, ct);
            if (!channelExists)
            {
                db.Channels.Add(new ChannelDB
                {
                    Id = conversationId,
                    Title = "Direct chat",
                });
            }

            db.ChatMessages.Add(new ChatMessageDB
            {
                Role = "user",
                Content = exchange.UserMessage,
                ChannelId = conversationId,
                ClientType = exchange.Turn.Input.ClientType,
            });
            if (!string.IsNullOrEmpty(exchange.Completion.Content))
            {
                db.ChatMessages.Add(new ChatMessageDB
                {
                    Role = "assistant",
                    Content = exchange.Completion.Content,
                    ProviderMetadataJson = exchange.Completion.ProviderMetadataJson,
                    ChannelId = conversationId,
                    ClientType = exchange.Turn.Input.ClientType,
                    PromptTokens = exchange.Completion.Usage?.PromptTokens,
                    CompletionTokens = exchange.Completion.Usage?.CompletionTokens,
                });
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }
}
