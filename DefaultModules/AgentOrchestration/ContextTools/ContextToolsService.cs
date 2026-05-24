using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Wraps cross-thread context and utility operations for the context-tools
/// inline tools. Rolled into agent-orchestration from the former
/// <c>sharpclaw_context_tools</c> module.
/// </summary>
internal sealed class ContextToolsService(
    IContextDataReader dataReader,
    IConfiguration? configuration = null)
{
    public static async Task<string> WaitAsync(
        JsonElement parameters, CancellationToken ct)
    {
        var seconds = 5;

        if (parameters.TryGetProperty("seconds", out var secEl)
            && secEl.TryGetInt32(out var s))
            seconds = s;

        seconds = Math.Clamp(seconds, 1, 300);

        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);

        return $"Waited {seconds} second{(seconds == 1 ? "" : "s")}.";
    }

    public async Task<string> ListAccessibleThreadsAsync(
        Guid agentId, Guid channelId, CancellationToken ct)
    {
        var threads = await dataReader.GetAccessibleThreadsAsync(agentId, channelId, ct);
        if (threads.Count == 0)
            return "No accessible threads found. Either the agent lacks the ReadCrossThreadHistory permission, or no other channels have opted in.";

        var result = threads.Select(t => new
        {
            threadId = t.ThreadId.ToString("D"),
            threadName = t.ThreadName,
            channelId = t.ChannelId.ToString("D"),
            channelTitle = t.ChannelTitle,
        });

        return JsonSerializer.Serialize(result);
    }

    public async Task<string> FormatAccessibleThreadsHeaderAsync(
        Guid agentId, Guid channelId, CancellationToken ct)
    {
        if (configuration?.GetValue<bool>("AgentOrchestration:DisableAccessibleThreadsHeader") == true)
            return "";

        var threads = await dataReader.GetAccessibleThreadsAsync(agentId, channelId, ct);
        if (threads.Count == 0)
            return "(none)";

        return string.Join(", ", threads.Select(
            t => $"{t.ThreadName} [{t.ChannelTitle}] ({t.ThreadId:D})"));
    }

    public async Task<string> ReadThreadHistoryAsync(
        JsonElement parameters, Guid agentId, Guid channelId, CancellationToken ct)
    {
        Guid threadId = Guid.Empty;
        int maxMessages = 50;

        if (parameters.TryGetProperty("threadId", out var tidEl))
            Guid.TryParse(tidEl.GetString(), out threadId);
        if (parameters.TryGetProperty("maxMessages", out var maxEl)
            && maxEl.TryGetInt32(out var mm))
            maxMessages = Math.Clamp(mm, 1, 200);

        if (threadId == Guid.Empty)
            return "Error: threadId is required.";

        var thread = await dataReader.GetAccessibleThreadAsync(agentId, channelId, threadId, ct);
        if (thread is null)
            return "Error: thread not found or not accessible to this agent.";

        var messages = await dataReader.GetThreadMessagesAsync(threadId, maxMessages, ct);

        if (messages.Count == 0)
            return "Thread exists but has no messages.";

        var result = messages.Select(m => new
        {
            role = m.Role,
            content = m.Content,
            sender = m.Sender,
            timestamp = m.Timestamp,
        });

        return JsonSerializer.Serialize(result);
    }
}
