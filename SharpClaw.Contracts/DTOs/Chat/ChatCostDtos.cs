namespace SharpClaw.Contracts.DTOs.Chat;

/// <summary>
/// Token usage breakdown for a single agent within a channel or thread.
/// </summary>
public sealed record AgentTokenBreakdown(
    Guid AgentId,
    string AgentName,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);

/// <summary>
/// Aggregated token usage for a channel, with per-agent breakdown.
/// </summary>
public sealed record ChannelCostResponse(
    Guid ChannelId,
    int TotalPromptTokens,
    int TotalCompletionTokens,
    int TotalTokens,
    IReadOnlyList<AgentTokenBreakdown> AgentBreakdown);

/// <summary>
/// Aggregated token usage for a thread, with per-agent breakdown.
/// </summary>
public sealed record ThreadCostResponse(
    Guid ThreadId,
    Guid ChannelId,
    int TotalPromptTokens,
    int TotalCompletionTokens,
    int TotalTokens,
    IReadOnlyList<AgentTokenBreakdown> AgentBreakdown);
