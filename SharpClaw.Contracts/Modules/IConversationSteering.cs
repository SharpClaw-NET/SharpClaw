using SharpClaw.Contracts.DTOs.Chat;

namespace SharpClaw.Contracts.Modules;

public interface IConversationSteering
{
    Task<ConversationSteeringResponse> AddAsync(
        ConversationSteeringRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<ConversationSteeringResponse>> ListAsync(
        Guid channelId,
        Guid? threadId = null,
        int limit = 20,
        CancellationToken ct = default);
}
