using SharpClaw.Contracts.DTOs.Agents;

namespace SharpClaw.Contracts.DTOs.Channels;

public sealed record CreateChannelRequest(
    Guid? AgentId = null,
    string? Title = null,
    Guid? ContextId = null,
    Guid? PermissionSetId = null,
    IReadOnlyList<Guid>? AllowedAgentIds = null,
    bool? DisableChatHeader = null,
    string? CustomId = null,
    string? CustomChatHeader = null,
    Guid? ToolAwarenessSetId = null,
    bool? DisableToolSchemas = null);

public sealed record UpdateChannelRequest(
    string? Title = null,
    Guid? ContextId = null,
    Guid? PermissionSetId = null,
    IReadOnlyList<Guid>? AllowedAgentIds = null,
    bool? DisableChatHeader = null,
    string? CustomId = null,
    string? CustomChatHeader = null,
    Guid? ToolAwarenessSetId = null,
    bool? DisableToolSchemas = null);

public sealed record ChannelResponse(
    Guid Id,
    string Title,
    AgentSummary? Agent,
    Guid? ContextId,
    string? ContextName,
    Guid? PermissionSetId,
    Guid? EffectivePermissionSetId,
    IReadOnlyList<AgentSummary> AllowedAgents,
    bool DisableChatHeader,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CustomId = null,
    string? CustomChatHeader = null,
    Guid? ToolAwarenessSetId = null,
    bool DisableToolSchemas = false);

// ── Granular operation DTOs

public sealed record SetChannelAgentRequest(Guid AgentId);

public sealed record AddAllowedAgentRequest(Guid AgentId);

public sealed record ChannelAllowedAgentsResponse(
    Guid ChannelId,
    AgentSummary? DefaultAgent,
    IReadOnlyList<AgentSummary> AllowedAgents);
