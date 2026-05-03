namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Thread info for cross-thread history access. Returned by
/// <see cref="SharpClaw.Contracts.Chat.IChatProcessingBridge.GetAccessibleThreadsAsync"/>
/// so core chat-pipeline call sites never need to know which module
/// owns the underlying cross-thread visibility policy.
/// </summary>
public sealed record ThreadSummary(
    Guid ThreadId,
    string ThreadName,
    Guid ChannelId,
    string ChannelTitle);
