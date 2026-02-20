using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Contexts;

// ── Context CRUD ─────────────────────────────────────────────────

public sealed record CreateContextRequest(
    Guid AgentId,
    string? Name = null,
    Guid? PermissionSetId = null);

public sealed record UpdateContextRequest(
    string? Name = null,
    Guid? PermissionSetId = null);

public sealed record ContextResponse(
    Guid Id,
    string Name,
    Guid AgentId,
    string AgentName,
    Guid? PermissionSetId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ── Effective permission (resolved view) ─────────────────────────

/// <summary>
/// The effective permission for an action type after resolving the
/// context → conversation / task override chain.
/// </summary>
public sealed record EffectivePermissionResponse(
    AgentActionType ActionType,
    PermissionClearance GrantedClearance,
    Guid? ResourceId,
    string Source);
