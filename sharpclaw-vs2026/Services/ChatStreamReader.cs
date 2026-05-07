using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace SharpClaw.VS2026Extension.Services;

/// <summary>
/// Parses a server-sent event (SSE) stream produced by the SharpClaw
/// <c>/channels/{id}/chat/stream</c> and thread-scoped stream endpoints
/// into a sequence of <see cref="ChatStreamEvent"/> values.
/// </summary>
internal static class ChatStreamReader
{
    /// <summary>
    /// Reads SSE events from <paramref name="response"/> and yields each
    /// parsed <see cref="ChatStreamEvent"/>. The response must have been
    /// obtained with <c>HttpCompletionOption.ResponseHeadersRead</c>.
    /// </summary>
    public static async IAsyncEnumerable<ChatStreamEvent> ReadAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? eventName = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;            // stream ended

            if (line.Length == 0)
            {
                // Empty line = SSE dispatch boundary; reset for next event
                eventName = null;
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line.Substring(7).Trim();
                continue;
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal) && eventName is not null)
            {
                var data = line.Substring(6);
                var evt = ParseEvent(eventName, data);
                if (evt is not null)
                    yield return evt;
            }
        }
    }

    private static ChatStreamEvent? ParseEvent(string eventName, string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            return eventName switch
            {
                "TextDelta" => new ChatStreamEvent
                {
                    Type = ChatStreamEventType.TextDelta,
                    Delta = root.TryGetProperty("delta", out var d) ? d.GetString() : null
                },
                "ToolCallStart" => new ChatStreamEvent
                {
                    Type = ChatStreamEventType.ToolCallStart,
                    ToolName = TryGetString(root, "job", "actionKey"),
                    ToolStatus = TryGetString(root, "job", "status") ?? "started"
                },
                "ToolCallResult" => new ChatStreamEvent
                {
                    Type = ChatStreamEventType.ToolCallResult,
                    ToolName = TryGetString(root, "result", "actionKey"),
                    ToolStatus = TryGetString(root, "result", "status") ?? "done"
                },
                "ApprovalRequired" => new ChatStreamEvent
                {
                    Type = ChatStreamEventType.ApprovalRequired,
                    ToolName = TryGetString(root, "pendingJob", "actionKey")
                },
                "ApprovalResult" => new ChatStreamEvent
                {
                    Type = ChatStreamEventType.ApprovalResult,
                    ToolName = TryGetString(root, "approvalOutcome", "actionKey"),
                    ToolStatus = TryGetString(root, "approvalOutcome", "status") ?? "resolved"
                },
                "Error" => new ChatStreamEvent
                {
                    Type = ChatStreamEventType.Error,
                    Error = TryGetString(root, "error")
                },
                "Done" => new ChatStreamEvent
                {
                    Type = ChatStreamEventType.Done,
                    FinalText = ExtractFinalText(root)
                },
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractFinalText(JsonElement root)
    {
        if (!root.TryGetProperty("finalResponse", out var fr) || fr.ValueKind == JsonValueKind.Null)
            return null;
        if (!fr.TryGetProperty("assistantMessage", out var am) || am.ValueKind == JsonValueKind.Null)
            return null;
        return am.TryGetProperty("content", out var content) ? content.GetString() : null;
    }

    private static string? TryGetString(JsonElement root, string prop)
    {
        return root.TryGetProperty(prop, out var el) && el.ValueKind != JsonValueKind.Null
            ? el.GetString() : null;
    }

    private static string? TryGetString(JsonElement root, string outer, string inner)
    {
        if (!root.TryGetProperty(outer, out var outerEl)) return null;
        if (outerEl.ValueKind == JsonValueKind.Null) return null;
        return outerEl.TryGetProperty(inner, out var innerEl) && innerEl.ValueKind != JsonValueKind.Null
            ? innerEl.GetString() : null;
    }
}

internal sealed class ChatStreamEvent
{
    public ChatStreamEventType Type { get; init; }
    public string? Delta { get; init; }
    public string? ToolName { get; init; }
    public string? ToolStatus { get; init; }
    public string? Error { get; init; }
    /// <summary>
    /// Final assistant text from the <c>Done</c> event's <c>finalResponse</c>
    /// payload. When present the UI replaces the streamed text with this
    /// authoritative value.
    /// </summary>
    public string? FinalText { get; init; }
}

internal enum ChatStreamEventType
{
    TextDelta,
    ToolCallStart,
    ToolCallResult,
    ApprovalRequired,
    ApprovalResult,
    Error,
    Done
}
