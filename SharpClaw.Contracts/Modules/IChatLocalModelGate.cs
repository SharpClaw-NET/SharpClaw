namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Module→host chat lifecycle gate for local-inference models. Implemented
/// by the LlamaSharp module to ensure the model is loaded before chat
/// completion and to release reference counts after the chat completes.
/// Optional: <c>ChatService</c> treats absence of this service as "no
/// local-inference module enabled" and skips the gate calls.
/// </summary>
public interface IChatLocalModelGate
{
    Task EnsureReadyForChatAsync(Guid modelId, CancellationToken ct = default);

    void ReleaseAfterChat(Guid modelId);
}
