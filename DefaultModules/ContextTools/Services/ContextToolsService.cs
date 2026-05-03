using System.Text.Json;

namespace SharpClaw.Modules.ContextTools.Services;

/// <summary>
/// Wraps cross-thread context and utility operations for the
/// Context Tools module (inline tools).
/// </summary>
internal sealed class ContextToolsService(ContextDataReader dataReader)
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

        if (!await dataReader.ThreadExistsAsync(threadId, channelId, ct))
            return "Error: thread not found or does not belong to this channel.";

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
