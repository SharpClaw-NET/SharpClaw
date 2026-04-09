using SharpClaw.Contracts.DTOs.Agents;
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
    AgentSummary Agent,
    Guid? PermissionSetId,
    bool DisableChatHeader,
    IReadOnlyList<AgentSummary> AllowedAgents,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ── Granular operation DTOs ──────────────────────────────────────

public sealed record AddContextAllowedAgentRequest(Guid AgentId);

public sealed record ContextAllowedAgentsResponse(
    Guid ContextId,
    AgentSummary DefaultAgent,
    IReadOnlyList<AgentSummary> AllowedAgents);

// ── Effective permission (resolved view) ─────────────────────────

/// <summary>
/// The effective permission for an action type after resolving the
/// context → channel / task override chain.
/// </summary>
public sealed record EffectivePermissionResponse(
    string ActionKey,
    PermissionClearance GrantedClearance,
    Guid? ResourceId,
    string Source);
