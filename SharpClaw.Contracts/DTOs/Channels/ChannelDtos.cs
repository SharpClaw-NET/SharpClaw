namespace SharpClaw.Contracts.DTOs.Channels;

public sealed record CreateChannelRequest(
    Guid? AgentId = null,
    string? Title = null,
    Guid? ContextId = null,
    Guid? PermissionSetId = null,
    IReadOnlyList<Guid>? AllowedAgentIds = null,
    bool? DisableChatHeader = null);

public sealed record UpdateChannelRequest(
    string? Title = null,
    Guid? ContextId = null,
    Guid? PermissionSetId = null,
    IReadOnlyList<Guid>? AllowedAgentIds = null,
    bool? DisableChatHeader = null);

public sealed record ChannelResponse(
    Guid Id,
    string Title,
    Guid? AgentId,
    string? AgentName,
    Guid? ContextId,
    string? ContextName,
    Guid? PermissionSetId,
    Guid? EffectivePermissionSetId,
    IReadOnlyList<Guid> AllowedAgentIds,
    bool DisableChatHeader,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
