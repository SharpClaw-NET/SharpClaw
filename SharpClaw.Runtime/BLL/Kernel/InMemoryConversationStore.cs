using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


/// <summary>Stores direct-chat exchanges in a bounded process-local history.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, List<ChatCompletionMessage>> _history = [];

    public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>(
                _history.TryGetValue(conversationId, out var messages)
                    ? messages.ToArray()
                    : []);
        }
    }

    public ValueTask CommitExchangeAsync(
        ChatExchange exchange,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(exchange);
        lock (_sync)
        {
            if (!_history.TryGetValue(exchange.Turn.Conversation.ConversationId, out var messages))
            {
                messages = [];
                _history.Add(exchange.Turn.Conversation.ConversationId, messages);
            }

            messages.Add(new ChatCompletionMessage("user", exchange.UserMessage));
            if (!string.IsNullOrEmpty(exchange.Completion.Content))
                messages.Add(new ChatCompletionMessage("assistant", exchange.Completion.Content));
        }

        return ValueTask.CompletedTask;
    }
}
