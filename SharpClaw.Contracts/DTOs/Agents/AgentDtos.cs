using System.Text.Json;

namespace SharpClaw.Contracts.DTOs.Agents;

public sealed record CreateAgentRequest(
    string Name,
    Guid ModelId,
    string? SystemPrompt = null,
    int? MaxCompletionTokens = null,
    string? CustomId = null,
    float? Temperature = null,
    float? TopP = null,
    int? TopK = null,
    float? FrequencyPenalty = null,
    float? PresencePenalty = null,
    string[]? Stop = null,
    int? Seed = null,
    JsonElement? ResponseFormat = null,
    string? ReasoningEffort = null,
    Dictionary<string, JsonElement>? ProviderParameters = null,
    string? CustomChatHeader = null);

public sealed record UpdateAgentRequest(
    string? Name = null,
    Guid? ModelId = null,
    string? SystemPrompt = null,
    int? MaxCompletionTokens = null,
    string? CustomId = null,
    float? Temperature = null,
    float? TopP = null,
    int? TopK = null,
    float? FrequencyPenalty = null,
    float? PresencePenalty = null,
    string[]? Stop = null,
    int? Seed = null,
    JsonElement? ResponseFormat = null,
    string? ReasoningEffort = null,
    Dictionary<string, JsonElement>? ProviderParameters = null,
    string? CustomChatHeader = null);

public sealed record AssignAgentRoleRequest(Guid RoleId);

public sealed record AgentResponse(
    Guid Id,
    string Name,
    string? SystemPrompt,
    Guid ModelId,
    string ModelName,
    string ProviderName,
    Guid? RoleId = null,
    string? RoleName = null,
    int? MaxCompletionTokens = null,
    string? CustomId = null,
    float? Temperature = null,
    float? TopP = null,
    int? TopK = null,
    float? FrequencyPenalty = null,
    float? PresencePenalty = null,
    string[]? Stop = null,
    int? Seed = null,
    JsonElement? ResponseFormat = null,
    string? ReasoningEffort = null,
    Dictionary<string, JsonElement>? ProviderParameters = null,
    string? CustomChatHeader = null);

/// <summary>
/// Lightweight agent summary
/// consumers don't need additional requests to resolve agent details.
/// Excludes <c>SystemPrompt</c> to keep payloads compact.
/// </summary>
public sealed record AgentSummary(
    Guid Id,
    string Name,
    Guid ModelId,
    string ModelName,
    string ProviderName,
    Guid? RoleId = null,
    string? RoleName = null,
    int? MaxCompletionTokens = null,
    string? CustomId = null,
    float? Temperature = null,
    float? TopP = null,
    int? TopK = null,
    float? FrequencyPenalty = null,
    float? PresencePenalty = null,
    string[]? Stop = null,
    int? Seed = null,
    JsonElement? ResponseFormat = null,
    string? ReasoningEffort = null,
    Dictionary<string, JsonElement>? ProviderParameters = null,
    string? CustomChatHeader = null);
