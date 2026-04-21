using System.Text;
using System.Text.Json;
using LLama.Common;
using SharpClaw.Application.Core.Clients;

namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Builds the <see cref="ChatHistory"/> for tool-enabled turns using the
/// SharpClaw envelope convention.
/// <para>
/// Appends a system-prompt suffix that defines the envelope contract and
/// lists available tools. Prior tool calls in history are injected as
/// assistant messages containing envelope-formatted JSON. Prior tool
/// results are injected as user messages containing a minimal JSON result
/// structure. The GGUF chat template is still applied by
/// <c>PromptTemplateTransformer</c> for outer conversation formatting —
/// only the content inside turns changes here.
/// </para>
/// </summary>
internal static class LlamaSharpToolPromptBuilder
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Builds a <see cref="ChatHistory"/> from tool-aware messages and
    /// tool definitions, injecting the envelope system-prompt suffix and
    /// formatting prior tool round-trips as envelope JSON.
    /// </summary>
    /// <param name="imageCount">
    /// Number of images staged into the MTMD projector for this turn.
    /// When greater than zero, a short notice is appended to the system
    /// prompt so the model is aware of the attached visual inputs.
    /// </param>
    /// <param name="strictTools">
    /// When <see langword="true"/>, per-tool argument schemas are
    /// enforced by the composite GBNF grammar and the prose parameter
    /// bullets are omitted — the grammar is ground truth and the
    /// bullets would only waste prompt tokens.
    /// </param>
    /// <param name="allowRefusal">
    /// When <see langword="true"/>, the system prompt describes the
    /// third <c>"refusal"</c> envelope shape so the model is aware it
    /// can decline cleanly instead of emitting plain-text refusals.
    /// </param>
    public static ChatHistory Build(
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int imageCount = 0,
        bool strictTools = false,
        bool allowRefusal = false)
    {
        var history = new ChatHistory();

        var systemContent = BuildSystemPrompt(systemPrompt, tools, imageCount, strictTools, allowRefusal);
        history.AddMessage(AuthorRole.System, systemContent);

        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case "tool":
                    history.AddMessage(AuthorRole.User, FormatToolResult(msg));
                    break;

                case "assistant" when msg.ToolCalls is { Count: > 0 }:
                    history.AddMessage(AuthorRole.Assistant, FormatAssistantToolCalls(msg));
                    break;

                case "assistant":
                    history.AddMessage(AuthorRole.Assistant, FormatAssistantMessage(msg.Content));
                    break;

                default:
                    history.AddMessage(MapRole(msg.Role ?? "user"), msg.Content ?? "");
                    break;
            }
        }

        return history;
    }

    // ── System prompt ─────────────────────────────────────────────

    private static string BuildSystemPrompt(
        string? systemPrompt,
        IReadOnlyList<ChatToolDefinition> tools,
        int imageCount,
        bool strictTools,
        bool allowRefusal)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            sb.Append(systemPrompt);
            sb.Append("\n\n");
        }

        if (imageCount > 0)
        {
            sb.Append("## Visual context\n\n");
            sb.Append(imageCount == 1
                ? "You have been shown 1 image in this turn. Refer to it naturally in your response.\n\n"
                : $"You have been shown {imageCount} images in this turn, in the order they appear in the conversation. Refer to them naturally in your response.\n\n");
        }

        sb.AppendLine("## Tool calling");
        sb.AppendLine();
        sb.AppendLine("You must respond with a JSON object in exactly this shape:");
        sb.AppendLine();
        sb.AppendLine("When responding with text only:");
        sb.AppendLine("""{"mode":"message","text":"<your response here>","calls":[]}""");
        sb.AppendLine();
        sb.AppendLine("When calling one or more tools:");
        sb.AppendLine("""{"mode":"tool_calls","text":"","calls":[{"id":"call_<unique>","name":"<tool_name>","args":{<arguments>}}]}""");
        sb.AppendLine();
        if (allowRefusal)
        {
            sb.AppendLine("When declining to respond (policy violation, unsafe or out-of-scope request):");
            sb.AppendLine("""{"mode":"refusal","text":"<short reason>","calls":[]}""");
            sb.AppendLine();
        }
        sb.AppendLine("Rules:");
        sb.AppendLine("- Every response must be valid JSON matching one of the shapes above.");
        if (allowRefusal)
            sb.AppendLine("- `mode` is one of: \"message\", \"tool_calls\", \"refusal\".");
        else
            sb.AppendLine("- `mode` is always either \"message\" or \"tool_calls\".");
        sb.AppendLine("- `text` is always a string (empty string when mode is \"tool_calls\").");
        sb.AppendLine("- `calls` is always an array (empty when mode is \"message\" or \"refusal\").");
        sb.AppendLine("- `args` is always a JSON object, never a JSON-encoded string.");
        sb.AppendLine("- Generate a unique `id` for each call (e.g. call_abc123).");
        sb.AppendLine("- Do not include any text outside the JSON object.");
        sb.AppendLine();

        if (tools.Count > 0)
        {
            sb.AppendLine("## Available tools");
            sb.AppendLine();

            foreach (var tool in tools)
            {
                sb.Append("### ");
                sb.AppendLine(tool.Name);
                sb.AppendLine(tool.Description);

                // In strict mode the composite GBNF grammar enforces each
                // tool's argument schema exactly — listing parameter
                // bullets only burns prompt tokens. Reference:
                // docs/internal/llamasharp-implementable-gaps.md §1.
                if (!strictTools
                    && tool.ParametersSchema.ValueKind == JsonValueKind.Object
                    && tool.ParametersSchema.TryGetProperty("properties", out var props)
                    && props.ValueKind == JsonValueKind.Object)
                {
                    sb.AppendLine("Parameters:");
                    foreach (var param in props.EnumerateObject())
                    {
                        var typeName = "any";
                        if (param.Value.TryGetProperty("type", out var typeEl))
                            typeName = typeEl.GetString() ?? "any";

                        var description = "";
                        if (param.Value.TryGetProperty("description", out var descEl))
                            description = descEl.GetString() ?? "";

                        sb.Append("  - `");
                        sb.Append(param.Name);
                        sb.Append("` (");
                        sb.Append(typeName);
                        sb.Append(')');
                        if (!string.IsNullOrEmpty(description))
                        {
                            sb.Append(": ");
                            sb.Append(description);
                        }
                        sb.AppendLine();
                    }
                }

                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── History formatting ────────────────────────────────────────

    /// <summary>
    /// Formats a prior plain-text assistant message as a <c>mode: "message"</c>
    /// envelope so that all assistant turns in history are consistent with the
    /// envelope contract defined in the system prompt. Without this, messages
    /// persisted from a prior session would appear as bare prose, contradicting
    /// the model's own instructions and causing drift on subsequent tool rounds.
    /// </summary>
    private static string FormatAssistantMessage(string? content) =>
        JsonSerializer.Serialize(
            new { mode = "message", text = content ?? "", calls = Array.Empty<object>() },
            _jsonOpts);

    /// <summary>
    /// Formats a prior assistant message that contains tool calls as
    /// envelope-formatted JSON (tool_calls mode).
    /// </summary>
    private static string FormatAssistantToolCalls(ToolAwareMessage msg)
    {
        var calls = msg.ToolCalls!.Select(tc => new
        {
            id = tc.Id,
            name = tc.Name,
            args = ParseArgsOrEmpty(tc.ArgumentsJson),
        });

        var envelope = new
        {
            mode = "tool_calls",
            text = msg.Content ?? "",
            calls,
        };

        return JsonSerializer.Serialize(envelope, _jsonOpts);
    }

    /// <summary>
    /// Formats a tool-result message as a user-turn JSON structure that
    /// the model was told to expect (matches the envelope result shape).
    /// When the message carries an image, a marker is prepended so the
    /// model knows visual data is present in the turn.
    /// </summary>
    private static string FormatToolResult(ToolAwareMessage msg)
    {
        var content = msg.Content ?? string.Empty;
        if (msg.HasImage)
            content = $"[Image attached]\n{content}";
        return JsonSerializer.Serialize(
            new { tool_result = new { id = msg.ToolCallId, content } },
            _jsonOpts);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static AuthorRole MapRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => AuthorRole.System,
        "assistant" => AuthorRole.Assistant,
        _ => AuthorRole.User,
    };

    /// <summary>
    /// Parses the <paramref name="argsJson"/> string as a
    /// <see cref="JsonElement"/>. Returns an empty object element when the
    /// string is null, empty, or not valid JSON.
    /// </summary>
    private static JsonElement ParseArgsOrEmpty(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return EmptyObject();

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
