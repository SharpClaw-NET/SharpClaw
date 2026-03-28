using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Bots;

public sealed record CreateBotIntegrationRequest(
    string Name,
    BotType BotType,
    string? BotToken = null,
    bool Enabled = true);

public sealed record UpdateBotIntegrationRequest(
    string? Name = null,
    bool? Enabled = null,
    string? BotToken = null,
    Guid? DefaultChannelId = null,
    Guid? DefaultThreadId = null);

public sealed record BotIntegrationResponse(
    Guid Id,
    string Name,
    BotType BotType,
    bool Enabled,
    bool HasBotToken,
    Guid? DefaultChannelId,
    Guid? DefaultThreadId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
