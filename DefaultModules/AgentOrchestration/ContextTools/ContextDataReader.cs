using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// A single chat message returned by <see cref="ContextDataReader.GetThreadMessagesAsync"/>.
/// Module-internal record; never crosses the host boundary.
/// </summary>
internal sealed record ChatMessageSummary(
    string Role,
    string Content,
    string Sender,
    DateTimeOffset Timestamp);

internal interface IContextDataReader
{
    Task<ThreadSummary?> GetAccessibleThreadAsync(
        Guid agentId,
        Guid currentChannelId,
        Guid threadId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ChatMessageSummary>> GetThreadMessagesAsync(
        Guid threadId,
        int maxMessages,
        CancellationToken ct = default);

    Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId,
        Guid currentChannelId,
        CancellationToken ct = default);
}

/// <summary>
/// Module-owned read service that backs the context-tools inline tools.
/// Reads directly from the host's <see cref="ISharpClawDataContext"/>
/// surface so the cross-thread visibility policy and the
/// <c>CanReadCrossThreadHistory</c> permission key live with the module
/// that owns them. Rolled into agent-orchestration from the former
/// <c>sharpclaw_context_tools</c> module.
/// </summary>
internal sealed class ContextDataReader(ISharpClawDataContext data) : IContextDataReader
{
    public async Task<ThreadSummary?> GetAccessibleThreadAsync(
        Guid agentId, Guid currentChannelId, Guid threadId, CancellationToken ct = default)
    {
        var accessibleThreads = await GetAccessibleThreadsAsync(agentId, currentChannelId, ct);
        return accessibleThreads.FirstOrDefault(t => t.ThreadId == threadId);
    }

    public async Task<IReadOnlyList<ChatMessageSummary>> GetThreadMessagesAsync(
        Guid threadId, int maxMessages, CancellationToken ct = default)
    {
        return await data.ChatMessages
            .Where(m => m.ThreadId == threadId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxMessages)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessageSummary(
                m.Role,
                m.Content,
                m.SenderUsername ?? m.SenderAgentName ?? "unknown",
                m.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId, Guid currentChannelId, CancellationToken ct = default)
    {
        var agentWithRole = await data.Agents
            .Include(a => a.Role)
                .ThenInclude(r => r!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        var agentPs = agentWithRole?.Role?.PermissionSet;
        var crossThreadKey = ContextToolsPermissionKeys.CanReadCrossThreadHistory;
        if (agentPs is null || !agentPs.GlobalFlags.Any(f => f.FlagKey == crossThreadKey))
            return [];

        var isIndependent = (agentPs.GlobalFlags
            .FirstOrDefault(f => f.FlagKey == crossThreadKey)
            ?.Clearance ?? PermissionClearance.Unset) == PermissionClearance.Independent;

        var channels = await data.Channels
            .Include(c => c.AllowedAgents)
            .Include(c => c.PermissionSet)
                .ThenInclude(ps => ps!.GlobalFlags)
            .Include(c => c.AgentContext)
                .ThenInclude(ctx => ctx!.PermissionSet)
                    .ThenInclude(ps => ps!.GlobalFlags)
            .Include(c => c.AgentContext)
                .ThenInclude(ctx => ctx!.AllowedAgents)
            .Where(c => c.Id != currentChannelId)
            .Where(c =>
                c.AgentId == agentId ||
                c.AllowedAgents.Any(a => a.Id == agentId) ||
                (c.AgentId == null && c.AgentContext != null && c.AgentContext.AgentId == agentId) ||
                (!c.AllowedAgents.Any() && c.AgentContext != null &&
                    c.AgentContext.AllowedAgents.Any(a => a.Id == agentId)))
            .ToListAsync(ct);

        if (!isIndependent)
        {
            channels = channels
                .Where(c =>
                {
                    var effectivePs = c.PermissionSet ?? c.AgentContext?.PermissionSet;
                    return effectivePs?.GlobalFlags.Any(f => f.FlagKey == crossThreadKey) == true;
                })
                .ToList();
        }

        if (channels.Count == 0)
            return [];

        var channelIds = channels.Select(c => c.Id).ToList();
        var channelTitles = channels.ToDictionary(c => c.Id, c => c.Title);

        var threads = await data.ChatThreads
            .Where(t => channelIds.Contains(t.ChannelId))
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new { t.Id, t.Name, t.ChannelId })
            .ToListAsync(ct);

        return threads
            .Select(t => new ThreadSummary(t.Id, t.Name, t.ChannelId, channelTitles[t.ChannelId]))
            .ToList();
    }
}

internal sealed class HostContextDataReaderAdapter : IContextDataReader
{
    private const string AddressEnv = "SHARPCLAW_HOST_CAPABILITIES_ADDRESS";
    private const string TokenEnv = "SHARPCLAW_HOST_CAPABILITIES_TOKEN";
    private const string TokenHeaderName = "X-SharpClaw-Control-Token";
    private const string AccessibleThreadsPath = "/.sharpclaw/host/context/threads/accessible";
    private const string ThreadMessagesPath = "/.sharpclaw/host/context/threads/messages";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client;
    private readonly string _token;

    private HostContextDataReaderAdapter(Uri address, string token)
    {
        _client = new HttpClient
        {
            BaseAddress = address,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _token = token;
    }

    public static HostContextDataReaderAdapter? TryCreate()
    {
        var address = Environment.GetEnvironmentVariable(AddressEnv);
        var token = Environment.GetEnvironmentVariable(TokenEnv);
        return string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(token)
            ? null
            : new HostContextDataReaderAdapter(new Uri(address), token);
    }

    public async Task<ThreadSummary?> GetAccessibleThreadAsync(
        Guid agentId,
        Guid currentChannelId,
        Guid threadId,
        CancellationToken ct = default)
    {
        var accessibleThreads = await GetAccessibleThreadsAsync(agentId, currentChannelId, ct);
        return accessibleThreads.FirstOrDefault(t => t.ThreadId == threadId);
    }

    public async Task<IReadOnlyList<ChatMessageSummary>> GetThreadMessagesAsync(
        Guid threadId,
        int maxMessages,
        CancellationToken ct = default)
    {
        var messages = (await PostAsync<HostContextThreadMessagesRequest, HostContextMessagesResponse>(
            ThreadMessagesPath,
            new HostContextThreadMessagesRequest
            {
                ThreadId = threadId,
                MaxMessages = maxMessages,
            },
            ct)).Messages;

        return [.. messages.Select(message => new ChatMessageSummary(
            message.Role,
            message.Content,
            message.Sender,
            message.Timestamp))];
    }

    public Task<IReadOnlyList<ThreadSummary>> GetAccessibleThreadsAsync(
        Guid agentId,
        Guid currentChannelId,
        CancellationToken ct = default) =>
        ReadAccessibleThreadsAsync(agentId, currentChannelId, ct);

    private async Task<IReadOnlyList<ThreadSummary>> ReadAccessibleThreadsAsync(
        Guid agentId,
        Guid currentChannelId,
        CancellationToken ct)
    {
        var response = await PostAsync<HostContextAccessibleThreadsRequest, HostContextThreadsResponse>(
            AccessibleThreadsPath,
            new HostContextAccessibleThreadsRequest
            {
                AgentId = agentId,
                CurrentChannelId = currentChannelId,
                CrossThreadPermissionKey = ContextToolsPermissionKeys.CanReadCrossThreadHistory,
            },
            ct);

        return response.Threads;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        message.Headers.TryAddWithoutValidation(TokenHeaderName, _token);

        using var response = await _client.SendAsync(message, ct);
        var body = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SharpClaw host context call {path} failed with HTTP {(int)response.StatusCode}: {body}");
        }

        return JsonSerializer.Deserialize<TResponse>(body ?? "{}", JsonOptions)
            ?? throw new JsonException($"Host context call {path} returned invalid JSON.");
    }

    private sealed record HostContextAccessibleThreadsRequest
    {
        public Guid AgentId { get; init; }
        public Guid CurrentChannelId { get; init; }
        public string CrossThreadPermissionKey { get; init; } = string.Empty;
    }

    private sealed record HostContextThreadMessagesRequest
    {
        public Guid ThreadId { get; init; }
        public int MaxMessages { get; init; } = 50;
    }

    private sealed record HostContextThreadsResponse(IReadOnlyList<ThreadSummary> Threads);

    private sealed record HostContextMessagesResponse(
        IReadOnlyList<HostContextChatMessageSummary> Messages);

    private sealed record HostContextChatMessageSummary(
        string Role,
        string Content,
        string Sender,
        DateTimeOffset Timestamp);
}
