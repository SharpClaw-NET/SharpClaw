namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Thread info for module-owned cross-thread history access.
/// </summary>
public sealed record ThreadSummary(
    Guid ThreadId,
    string ThreadName,
    Guid ChannelId,
    string ChannelTitle);
