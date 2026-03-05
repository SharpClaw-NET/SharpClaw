namespace SharpClaw.Contracts.DTOs.Threads;

public sealed record CreateThreadRequest(
    string? Name = null,
    int? MaxMessages = null,
    int? MaxCharacters = null);

public sealed record UpdateThreadRequest(
    string? Name = null,
    int? MaxMessages = null,
    int? MaxCharacters = null);

public sealed record ThreadResponse(
    Guid Id,
    string Name,
    Guid ChannelId,
    int? MaxMessages,
    int? MaxCharacters,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
