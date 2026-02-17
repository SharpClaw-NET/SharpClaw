using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Contexts;

// ── Permission grant (shared shape) ──────────────────────────────

public sealed record PermissionGrantRequest(
    AgentActionType ActionType,
    PermissionClearance GrantedClearance);

public sealed record PermissionGrantResponse(
    Guid Id,
    AgentActionType ActionType,
    PermissionClearance GrantedClearance);

// ── Context CRUD ─────────────────────────────────────────────────

public sealed record CreateContextRequest(
    Guid AgentId,
    string? Name = null,
    IReadOnlyList<PermissionGrantRequest>? PermissionGrants = null);

public sealed record UpdateContextRequest(
    string? Name = null);

public sealed record ContextResponse(
    Guid Id,
    string Name,
    Guid AgentId,
    string AgentName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PermissionGrantResponse> PermissionGrants);

// ── Effective permission (resolved view) ─────────────────────────

/// <summary>
/// The effective permission for an action type after resolving the
/// context → conversation / task override chain.
/// </summary>
public sealed record EffectivePermissionResponse(
    AgentActionType ActionType,
    PermissionClearance GrantedClearance,
    string Source);
