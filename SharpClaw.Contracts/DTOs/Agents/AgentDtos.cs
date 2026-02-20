namespace SharpClaw.Contracts.DTOs.Agents;

public sealed record CreateAgentRequest(string Name, Guid ModelId, string? SystemPrompt = null);
public sealed record UpdateAgentRequest(string? Name = null, Guid? ModelId = null, string? SystemPrompt = null);
public sealed record AssignAgentRoleRequest(Guid RoleId, Guid CallerUserId);
public sealed record AgentResponse(Guid Id, string Name, string? SystemPrompt, Guid ModelId, string ModelName, string ProviderName, Guid? RoleId = null, string? RoleName = null);
