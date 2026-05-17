using System.Net.Http;
using System.Text;
using System.Text.Json;
using SharpClaw.Helpers;

namespace SharpClaw.Presentation;

public sealed record UnoCreateChannelRequest(string Title, Guid? AgentId);

public sealed record UnoCreateThreadRequest(string Name);

public sealed record UnoChatStreamRequest(string Path, UnoChatStreamRequestBody Body);

public sealed record UnoChatStreamRequestBody(string Message, Guid? AgentId, string ClientType);

public sealed record UnoChatInteractionState(
    bool IsSending,
    bool IsThreadBusy,
    bool HistoryStaleAfterSend);

public sealed record UnoThreadWatchDecision(
    UnoChatInteractionState State,
    bool LoadHistoryNow,
    bool LoadCostNow,
    bool ScrollToBottom,
    bool SendEnabled,
    bool InputEnabled);

public sealed record UnoStreamCostSnapshot(int? ChannelTokens, int? ThreadTokens);

public sealed record UnoSseEventResult(bool ShouldEnd, bool TextChanged);

public enum UnoJobActionKind
{
    Approve,
    Cancel,
    Stop,
    Pause,
    Resume,
}

public sealed record UnoJobActionRequest(
    HttpMethod Method,
    string Path,
    bool SendsEmptyJsonBody);

public static class UnoClientState
{
    private const int GeneratedNameLength = 10;

    public static UnoCreateChannelRequest CreateChannelRequest(string message, Guid? agentId)
        => new(TerminalUI.Truncate(message, GeneratedNameLength), agentId);

    public static UnoCreateThreadRequest CreateDefaultThreadRequest()
        => new("Default");

    public static UnoCreateThreadRequest CreatePendingThreadRequest(string message)
        => new(TerminalUI.Truncate(message, GeneratedNameLength));

    public static UnoChatStreamRequest CreateChatStreamRequest(
        Guid channelId,
        Guid? threadId,
        string message,
        Guid? agentId,
        string clientType)
    {
        var threadPart = threadId is { } tid ? $"/threads/{tid}" : "";
        return new(
            $"/channels/{channelId}/chat{threadPart}/stream",
            new UnoChatStreamRequestBody(message, agentId, clientType));
    }

    public static UnoThreadWatchDecision ApplyThreadWatchEvent(
        UnoChatInteractionState state,
        string eventType)
    {
        if (string.Equals(eventType, "Processing", StringComparison.Ordinal))
        {
            var next = state with { IsThreadBusy = true };
            return new(next, false, false, false, false, false);
        }

        if (string.Equals(eventType, "NewMessages", StringComparison.Ordinal))
        {
            var stale = state.IsSending;
            var next = state with
            {
                IsThreadBusy = false,
                HistoryStaleAfterSend = state.HistoryStaleAfterSend || stale,
            };

            return new(
                next,
                LoadHistoryNow: !state.IsSending,
                LoadCostNow: !state.IsSending,
                ScrollToBottom: !state.IsSending,
                SendEnabled: !state.IsSending,
                InputEnabled: !state.IsSending);
        }

        return new(
            state,
            LoadHistoryNow: false,
            LoadCostNow: false,
            ScrollToBottom: false,
            SendEnabled: !state.IsSending && !state.IsThreadBusy,
            InputEnabled: !state.IsSending && !state.IsThreadBusy);
    }

    public static UnoThreadWatchDecision CompleteSend(UnoChatInteractionState state)
    {
        var next = state with { IsSending = false, HistoryStaleAfterSend = false };
        return new(
            next,
            LoadHistoryNow: state.HistoryStaleAfterSend,
            LoadCostNow: state.HistoryStaleAfterSend,
            ScrollToBottom: state.HistoryStaleAfterSend,
            SendEnabled: !state.IsThreadBusy,
            InputEnabled: !state.IsThreadBusy);
    }

    public static IReadOnlyList<UnoJobActionKind> GetVisibleJobActions(string status)
        => status switch
        {
            "AwaitingApproval" => [UnoJobActionKind.Approve, UnoJobActionKind.Cancel],
            "Queued" or "Executing" => [UnoJobActionKind.Cancel, UnoJobActionKind.Stop, UnoJobActionKind.Pause],
            "Paused" => [UnoJobActionKind.Resume, UnoJobActionKind.Cancel],
            _ => [],
        };

    public static UnoJobActionRequest CreateJobActionRequest(
        Guid channelId,
        Guid jobId,
        UnoJobActionKind action)
    {
        var suffix = action switch
        {
            UnoJobActionKind.Approve => "approve",
            UnoJobActionKind.Cancel => "cancel",
            UnoJobActionKind.Stop => "stop",
            UnoJobActionKind.Pause => "pause",
            UnoJobActionKind.Resume => "resume",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

        var method = action switch
        {
            UnoJobActionKind.Pause or UnoJobActionKind.Resume => HttpMethod.Put,
            _ => HttpMethod.Post,
        };

        return new(
            method,
            $"/channels/{channelId}/jobs/{jobId}/{suffix}",
            SendsEmptyJsonBody: action == UnoJobActionKind.Approve);
    }
}

public sealed class UnoSseStreamState
{
    private readonly StringBuilder _builder = new();
    private bool _needsNewlineBeforeNextDelta;

    public string Text => _builder.ToString();
    public bool DoneReceived { get; private set; }
    public bool ErrorReceived { get; private set; }
    public string? ErrorText { get; private set; }
    public UnoStreamCostSnapshot? Cost { get; private set; }
    public bool ShouldLoadCostFallback => !DoneReceived && !ErrorReceived;

    public UnoSseEventResult Apply(string eventType, string dataJson)
    {
        switch (eventType)
        {
            case "TextDelta":
                return ApplyTextDelta(dataJson);
            case "ToolCallStart":
                return AppendStatusLine(dataJson, "job", "started");
            case "ToolCallResult":
                return AppendStatusLine(dataJson, "result", "done");
            case "ApprovalRequired":
                return AppendApprovalRequired(dataJson);
            case "ApprovalResult":
                return AppendStatusLine(dataJson, "approvalOutcome", "resolved");
            case "Error":
                return ApplyError(dataJson);
            case "Done":
                return ApplyDone(dataJson);
            default:
                return new(false, false);
        }
    }

    private UnoSseEventResult ApplyTextDelta(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var delta = doc.RootElement.GetProperty("delta").GetString();
        if (delta is null)
            return new(false, false);

        if (_needsNewlineBeforeNextDelta)
        {
            _builder.Append('\n');
            _needsNewlineBeforeNextDelta = false;
        }

        _builder.Append(delta);
        return new(false, true);
    }

    private UnoSseEventResult AppendStatusLine(
        string dataJson,
        string objectProperty,
        string defaultStatus)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var source = doc.RootElement.GetProperty(objectProperty);
        var actionKey = source.GetProperty("actionKey").GetString() ?? "?";
        var status = source.GetProperty("status").GetString() ?? defaultStatus;
        _builder.Append($"\n[{actionKey}] -> {status}");
        _needsNewlineBeforeNextDelta = true;
        return new(false, true);
    }

    private UnoSseEventResult AppendApprovalRequired(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var actionKey = doc.RootElement
            .GetProperty("pendingJob")
            .GetProperty("actionKey")
            .GetString() ?? "?";

        _builder.Append($"\n[{actionKey}] awaiting approval");
        _needsNewlineBeforeNextDelta = true;
        return new(false, true);
    }

    private UnoSseEventResult ApplyError(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        ErrorText = doc.RootElement.GetProperty("error").GetString() ?? "Unknown error";
        ErrorReceived = true;
        return new(true, true);
    }

    private UnoSseEventResult ApplyDone(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("finalResponse", out var finalResponse))
        {
            if (finalResponse.TryGetProperty("assistantMessage", out var message)
                && message.TryGetProperty("content", out var content))
            {
                var finalText = content.GetString();
                if (finalText is not null)
                {
                    _builder.Clear();
                    _builder.Append(finalText);
                }
            }

            Cost = new UnoStreamCostSnapshot(
                TryReadTotalTokens(finalResponse, "channelCost"),
                TryReadTotalTokens(finalResponse, "threadCost"));
        }

        DoneReceived = true;
        return new(true, true);
    }

    private static int? TryReadTotalTokens(JsonElement source, string property)
    {
        return source.TryGetProperty(property, out var cost)
            && cost.TryGetProperty("totalTokens", out var tokens)
            && tokens.TryGetInt32(out var value)
                ? value
                : null;
    }
}
