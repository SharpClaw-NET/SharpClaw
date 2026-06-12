namespace SharpClaw.Contracts.DTOs.Chat;

public sealed record ConversationSteeringRequest(
    Guid ChannelId,
    Guid? ThreadId,
    string Summary,
    string? Source = null,
    string? Category = null,
    string? Details = null,
    string? ClientType = null);

public sealed record ConversationSteeringResponse(
    Guid MessageId,
    Guid ChannelId,
    Guid? ThreadId,
    string Content,
    DateTimeOffset Timestamp,
    string? Source = null,
    string? Category = null);
