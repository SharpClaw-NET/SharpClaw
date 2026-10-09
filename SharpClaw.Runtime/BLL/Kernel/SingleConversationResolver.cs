using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


/// <summary>Provides one stable conversation when no feature registration is loaded.</summary>
public sealed class SingleConversationResolver(Guid conversationId) : IConversationResolver
{
    private readonly Guid _conversationId = conversationId == Guid.Empty
        ? throw new ArgumentException("The conversation identifier must not be empty.", nameof(conversationId))
        : conversationId;

    public ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ConversationSelection(
            input.ConversationId.GetValueOrDefault(_conversationId),
            input.ConversationId is null));
    }
}
