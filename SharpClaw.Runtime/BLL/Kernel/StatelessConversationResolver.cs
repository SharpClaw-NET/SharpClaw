using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


/// <summary>Resolves one independent conversation for each stateless turn.</summary>
internal sealed class StatelessConversationResolver : IConversationResolver
{
    public ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ConversationSelection(Guid.NewGuid(), Created: true));
    }
}
