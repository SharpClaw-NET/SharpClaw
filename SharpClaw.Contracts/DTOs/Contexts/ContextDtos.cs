using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Contexts;

// ── Context CRUD ─────────────────────────────────────────────────

public sealed record CreateContextRequest(
    Guid AgentId,
    string? Name = null,
    Guid? PermissionSetId = null,
    bool? DisableChatHeader = null,
    IReadOnlyList<Guid>? AllowedAgentIds = null);

public sealed record UpdateContextRequest(
    string? Name = null,
    Guid? PermissionSetId = null,
    bool? DisableChatHeader = null,
    IReadOnlyList<Guid>? AllowedAgentIds = null);

public sealed record ContextResponse(
    Guid Id,
    string Name,
    Guid AgentId,
    string AgentName,
    Guid? PermissionSetId,
    bool DisableChatHeader,
    IReadOnlyList<Guid> AllowedAgentIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ── Effective permission (resolved view) ─────────────────────────

/// <summary>
/// The effective permission for an action type after resolving the
/// context → channel / task override chain.
/// </summary>
public sealed record EffectivePermissionResponse(
    AgentActionType ActionType,
    PermissionClearance GrantedClearance,
    Guid? ResourceId,
    string Source);
