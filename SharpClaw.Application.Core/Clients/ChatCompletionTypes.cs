using System.Text.Json;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Describes a tool the model may invoke during a chat completion.
/// </summary>
public sealed record ChatToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersSchema);

/// <summary>
/// A tool invocation emitted by the model in a chat completion response.
/// </summary>
public sealed record ChatToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

/// <summary>
/// The result of a tool-aware chat completion. Contains either text
/// content, one or more tool calls, or both (some providers return
/// partial text alongside tool invocations).
/// </summary>
public sealed class ChatCompletionResult
{
    public string? Content { get; init; }
    public IReadOnlyList<ChatToolCall> ToolCalls { get; init; } = [];
    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>
    /// Token usage reported by the provider. <see langword="null"/> when
    /// the provider does not include usage data in the response.
    /// </summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>
/// Token counts returned by a provider for a single completion call.
/// </summary>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// A single chunk from a streaming chat completion. Either a text delta
/// or a final result containing accumulated tool calls.
/// </summary>
public sealed class ChatStreamChunk
{
    /// <summary>Partial text token. <see langword="null"/> on the final chunk.</summary>
    public string? Delta { get; init; }

    /// <summary>
    /// Set on the final chunk only. Contains the complete list of tool
    /// calls (if any) and the full accumulated content.
    /// </summary>
    public ChatCompletionResult? Finished { get; init; }

    public bool IsFinished => Finished is not null;

    public static ChatStreamChunk Text(string delta) => new() { Delta = delta };

    public static ChatStreamChunk Final(ChatCompletionResult result) =>
        new() { Finished = result };
}

/// <summary>
/// A message in a tool-aware conversation history. Represents system,
/// user, assistant (with optional tool calls), and tool-result messages.
/// </summary>
public sealed record ToolAwareMessage
{
    public required string Role { get; init; }
    public string? Content { get; init; }

    /// <summary>
    /// Tool calls emitted by the assistant. Present only when
    /// <see cref="Role"/> is <c>"assistant"</c>.
    /// </summary>
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// The ID of the tool call this message responds to. Present
    /// only when <see cref="Role"/> is <c>"tool"</c>.
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Optional base64-encoded image data attached to this message.
    /// When present, providers should send the image as a multipart
    /// content block (e.g. OpenAI <c>image_url</c>, Anthropic <c>image</c>).
    /// </summary>
    public string? ImageBase64 { get; init; }

    /// <summary>
    /// MIME type of <see cref="ImageBase64"/> (e.g. <c>"image/png"</c>).
    /// </summary>
    public string? ImageMediaType { get; init; }

    public bool HasImage => ImageBase64 is not null;

    public static ToolAwareMessage System(string content) =>
        new() { Role = "system", Content = content };

    public static ToolAwareMessage User(string content) =>
        new() { Role = "user", Content = content };

    public static ToolAwareMessage Assistant(string content) =>
        new() { Role = "assistant", Content = content };

    public static ToolAwareMessage AssistantWithToolCalls(
        IReadOnlyList<ChatToolCall> toolCalls, string? content = null) =>
        new() { Role = "assistant", Content = content, ToolCalls = toolCalls };

    public static ToolAwareMessage ToolResult(string toolCallId, string content) =>
        new() { Role = "tool", Content = content, ToolCallId = toolCallId };

    public static ToolAwareMessage ToolResultWithImage(
        string toolCallId, string content, string imageBase64, string mediaType = "image/png") =>
        new()
        {
            Role = "tool",
            Content = content,
            ToolCallId = toolCallId,
            ImageBase64 = imageBase64,
            ImageMediaType = mediaType
        };
}
