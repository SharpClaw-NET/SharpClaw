namespace SharpClaw.Contracts.DTOs.Channels;

public sealed record CreateChannelRequest(
    Guid AgentId,
    string? Title = null,
    Guid? ContextId = null,
    Guid? PermissionSetId = null,
    IReadOnlyList<Guid>? AllowedAgentIds = null);

public sealed record UpdateChannelRequest(
    string? Title = null,
    Guid? ContextId = null,
    Guid? PermissionSetId = null,
    IReadOnlyList<Guid>? AllowedAgentIds = null);

public sealed record ChannelResponse(
    Guid Id,
    string Title,
    Guid AgentId,
    string AgentName,
    Guid? ContextId,
    string? ContextName,
    Guid? PermissionSetId,
    Guid? EffectivePermissionSetId,
    IReadOnlyList<Guid> AllowedAgentIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
