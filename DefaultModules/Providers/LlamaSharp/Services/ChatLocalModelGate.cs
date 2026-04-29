using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Providers.LlamaSharp.Services;

/// <summary>
/// Module-side implementation of <see cref="IChatLocalModelGate"/> that
/// delegates to <see cref="LocalModelService"/>.
/// </summary>
public sealed class ChatLocalModelGate(LocalModelService inner) : IChatLocalModelGate
{
    public Task EnsureReadyForChatAsync(Guid modelId, CancellationToken ct = default) =>
        inner.EnsureReadyForChatAsync(modelId, ct);

    public void ReleaseAfterChat(Guid modelId) => inner.ReleaseAfterChat(modelId);
}
