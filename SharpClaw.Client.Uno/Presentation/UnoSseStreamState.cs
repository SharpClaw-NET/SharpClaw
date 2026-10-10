using System.Text.Json;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Presentation;


public sealed class UnoSseStreamState
{
    private static readonly JsonSerializerOptions KernelJson = new() { PropertyNameCaseInsensitive = true };
    private readonly System.Text.StringBuilder _builder = new();
    private bool _needsNewlineBeforeNextDelta;

    public string Text => _builder.ToString();
    public bool DoneReceived { get; private set; }
    public bool ErrorReceived { get; private set; }
    public string? ErrorText { get; private set; }

    public UnoSseEventResult Apply(string eventType, string dataJson)
    {
        return eventType switch
        {
            "" or "message" => ApplyKernelChunk(dataJson),
            "TextDelta" => ApplyTextDelta(dataJson),
            "ToolCallStart" => AppendStatusLine(dataJson, "job", "started"),
            "ToolCallResult" => AppendStatusLine(dataJson, "result", "done"),
            "ApprovalRequired" => AppendApprovalRequired(dataJson),
            "ApprovalResult" => AppendStatusLine(dataJson, "approvalOutcome", "resolved"),
            "Error" => ApplyError(dataJson),
            "Done" => ApplyDone(dataJson),
            _ => new UnoSseEventResult(false, false),
        };
    }

    private UnoSseEventResult ApplyKernelChunk(string dataJson)
    {
        var chunk = JsonSerializer.Deserialize<ChatStreamChunk>(dataJson, KernelJson)
            ?? throw new JsonException("A Runtime stream chunk is required.");
        if (chunk.IsFinished)
        {
            if (chunk.Finished!.Content is { } content)
            {
                _builder.Clear();
                _builder.Append(content);
            }
            DoneReceived = true;
            return new UnoSseEventResult(true, true);
        }
        if (chunk.Delta is not { } delta) return new UnoSseEventResult(false, false);
        _builder.Append(delta);
        return new UnoSseEventResult(false, true);
    }

    private UnoSseEventResult ApplyTextDelta(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        var delta = document.RootElement.GetProperty("delta").GetString();
        if (delta is null)
            return new UnoSseEventResult(false, false);

        if (_needsNewlineBeforeNextDelta)
        {
            _builder.Append('\n');
            _needsNewlineBeforeNextDelta = false;
        }

        _builder.Append(delta);
        return new UnoSseEventResult(false, true);
    }

    private UnoSseEventResult AppendStatusLine(
        string dataJson,
        string objectProperty,
        string defaultStatus)
    {
        using var document = JsonDocument.Parse(dataJson);
        var source = document.RootElement.GetProperty(objectProperty);
        var actionKey = source.GetProperty("actionKey").GetString() ?? "?";
        var status = source.GetProperty("status").GetString() ?? defaultStatus;
        _builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n[{actionKey}] -> {status}");
        _needsNewlineBeforeNextDelta = true;
        return new UnoSseEventResult(false, true);
    }

    private UnoSseEventResult AppendApprovalRequired(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        var actionKey = document.RootElement
            .GetProperty("pendingJob")
            .GetProperty("actionKey")
            .GetString() ?? "?";
        _builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n[{actionKey}] awaiting approval");
        _needsNewlineBeforeNextDelta = true;
        return new UnoSseEventResult(false, true);
    }

    private UnoSseEventResult ApplyError(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        ErrorText = document.RootElement.GetProperty("error").GetString() ?? "Unknown error";
        ErrorReceived = true;
        return new UnoSseEventResult(true, true);
    }

    private UnoSseEventResult ApplyDone(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        var root = document.RootElement;
        if (root.TryGetProperty("finalResponse", out var finalResponse) &&
            finalResponse.TryGetProperty("assistantMessage", out var message) &&
            message.TryGetProperty("content", out var content))
        {
            var finalText = content.GetString();
            if (finalText is not null)
            {
                _builder.Clear();
                _builder.Append(finalText);
            }
        }

        DoneReceived = true;
        return new UnoSseEventResult(true, true);
    }
}
