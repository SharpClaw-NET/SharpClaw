namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Generic host-side bridge for agent-related operations: chat, structured
/// response parsing, lookups, and provisioning of agents/threads/roles/channels.
/// Any module (or other host consumer) that needs to drive these flows resolves
/// this bridge from <see cref="ITaskStepExecutionContext.Services"/> instead of
/// taking a direct dependency on <c>SharpClawDbContext</c>, <c>ChatService</c>,
/// <c>RoleService</c>, etc.
///
/// The implementation lives in <c>SharpClaw.Application.Core</c> and is
/// registered in DI by the host. Multiple modules may share this bridge —
/// the <c>instanceId</c> / <c>taskName</c> parameters are advisory metadata
/// for logging and chat context, not module-specific state.
/// </summary>
public interface IHostAgentBridge
{
    Task<string?> ChatAsync(
        Guid instanceId,
        string taskName,
        string message,
        Guid? agentId,
        CancellationToken ct);

    Task<string> ChatStreamAsync(
        Guid instanceId,
        string taskName,
        string message,
        Guid? agentId,
        CancellationToken ct);

    Task<string?> ChatToThreadAsync(
        Guid instanceId,
        string taskName,
        Guid threadId,
        string message,
        Guid? agentId,
        CancellationToken ct);

    string ParseStructuredResponse(
        Guid instanceId,
        string text,
        string? typeName);

    Task<Guid?> FindModelAsync(string search, CancellationToken ct);
    Task<Guid?> FindProviderAsync(string search, CancellationToken ct);
    Task<Guid?> FindAgentAsync(string search, CancellationToken ct);
    Task<Guid?> FindRoleAsync(string search, CancellationToken ct);
    Task<Guid?> FindChannelAsync(string search, CancellationToken ct);

    Task<Guid> CreateAgentAsync(
        Guid instanceId,
        string name,
        Guid modelId,
        string? systemPrompt,
        string? customId,
        CancellationToken ct);

    Task<Guid> CreateThreadAsync(
        Guid instanceId,
        Guid? channelId,
        string? threadName,
        CancellationToken ct);

    Task<Guid> CreateRoleAsync(string roleName, CancellationToken ct);

    Task SetRolePermissionsAsync(
        Guid roleId,
        string requestJson,
        CancellationToken ct);

    Task AssignRoleAsync(
        Guid agentId,
        Guid roleId,
        CancellationToken ct);

    Task<Guid> CreateChannelAsync(
        Guid instanceId,
        string title,
        Guid agentId,
        string? customId,
        CancellationToken ct);

    Task AddAllowedAgentAsync(
        Guid instanceId,
        Guid agentId,
        Guid? channelId,
        CancellationToken ct);
}
