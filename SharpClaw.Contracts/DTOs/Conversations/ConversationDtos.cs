using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Conversations;

public sealed record CreateConversationRequest(
    Guid AgentId,
    string? Title = null,
    Guid? ModelId = null,
    Guid? ContextId = null,
    Guid? PermissionSetId = null);

public sealed record UpdateConversationRequest(
    string? Title = null,
    Guid? ModelId = null,
    Guid? ContextId = null,
    Guid? PermissionSetId = null);

public sealed record ConversationResponse(
    Guid Id,
    string Title,
    Guid AgentId,
    string AgentName,
    Guid ModelId,
    string ModelName,
    Guid? ContextId,
    string? ContextName,
    Guid? PermissionSetId,
    Guid? EffectivePermissionSetId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
