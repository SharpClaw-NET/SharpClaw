using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


/// <summary>Discards conversation state when the Context registration is absent.</summary>
internal sealed class StatelessConversationStore : IConversationStore
{
    public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>([]);
    }

    public ValueTask CommitExchangeAsync(
        ChatExchange exchange,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
