using SharpClaw.Contracts.Providers;

namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Request context supplied to module-owned custom header tag resolvers.
/// Modules can use this to resolve their own dynamic header values without
/// routing the decision through a core chat-processing bridge.
/// </summary>
/// <param name="ChannelId">Current channel id.</param>
/// <param name="ChannelTitle">Current channel title.</param>
/// <param name="AgentId">Agent handling the chat request.</param>
/// <param name="AgentName">Agent display name.</param>
/// <param name="ClientType">Client type reported by the caller.</param>
/// <param name="UserId">Current user id, when available.</param>
/// <param name="CompletionParameters">Provider completion parameters for the request.</param>
/// <param name="ProviderKey">Provider key used by the resolved model.</param>
public sealed record ModuleHeaderTagContext(
    Guid ChannelId,
    string ChannelTitle,
    Guid AgentId,
    string AgentName,
    string ClientType,
    Guid? UserId,
    CompletionParameters? CompletionParameters,
    string ProviderKey);
