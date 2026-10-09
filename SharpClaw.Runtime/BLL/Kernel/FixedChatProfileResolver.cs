using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


/// <summary>Resolves one configured direct-chat profile.</summary>
public sealed class FixedChatProfileResolver(ChatProfile profile) : IChatProfileResolver
{
    private readonly ChatProfile _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public ValueTask<ChatProfile> ResolveAsync(
        ChatTurnContext turn,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_profile);
    }
}
