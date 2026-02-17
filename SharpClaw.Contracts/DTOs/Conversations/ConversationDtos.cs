using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Conversations;

public sealed record ConversationPermissionGrantRequest(
    AgentActionType ActionType,
    PermissionClearance GrantedClearance);

public sealed record CreateConversationRequest(
    Guid AgentId,
    string? Title = null,
    Guid? ModelId = null,
    Guid? ContextId = null,
    IReadOnlyList<ConversationPermissionGrantRequest>? PermissionGrants = null);

public sealed record UpdateConversationRequest(
    string? Title = null,
    Guid? ModelId = null,
    Guid? ContextId = null);

public sealed record ConversationPermissionGrantResponse(
    Guid Id,
    AgentActionType ActionType,
    PermissionClearance GrantedClearance);

public sealed record ConversationResponse(
    Guid Id,
    string Title,
    Guid AgentId,
    string AgentName,
    Guid ModelId,
    string ModelName,
    Guid? ContextId,
    string? ContextName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationPermissionGrantResponse> PermissionGrants,
    IReadOnlyList<EffectivePermissionResponse> EffectivePermissions);
