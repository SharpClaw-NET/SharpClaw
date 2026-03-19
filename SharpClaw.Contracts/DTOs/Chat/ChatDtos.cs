using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Chat;

public sealed record ChatRequest(
    string Message,
    Guid? AgentId = null,
    ChatClientType ClientType = ChatClientType.API,
    EditorContext? EditorContext = null,
    TaskChatContext? TaskContext = null);
public sealed record ChatMessageResponse(
    string Role,
    string Content,
    DateTimeOffset Timestamp,
    Guid? SenderUserId = null,
    string? SenderUsername = null,
    Guid? SenderAgentId = null,
    string? SenderAgentName = null,
    string? ClientType = null);
public sealed record ChatResponse(
    ChatMessageResponse UserMessage,
    ChatMessageResponse AssistantMessage,
    IReadOnlyList<AgentJobResponse>? JobResults = null,
    ChannelCostResponse? ChannelCost = null,
    ThreadCostResponse? ThreadCost = null);
