using SharpClaw.Contracts.DTOs.AgentActions;

namespace SharpClaw.Contracts.DTOs.Chat;

public sealed record ChatRequest(string Message, Guid? AgentId = null);
public sealed record ChatMessageResponse(string Role, string Content, DateTimeOffset Timestamp);
public sealed record ChatResponse(
    ChatMessageResponse UserMessage,
    ChatMessageResponse AssistantMessage,
    IReadOnlyList<AgentJobResponse>? JobResults = null);
