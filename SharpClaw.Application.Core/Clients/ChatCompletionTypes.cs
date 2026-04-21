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

    /// <summary>
    /// Why generation stopped, normalised across providers. Defaults to
    /// <see cref="FinishReason.Unknown"/> when the provider does not
    /// report one. See <see cref="FinishReason"/> for the mapping table.
    /// </summary>
    public FinishReason FinishReason { get; init; } = FinishReason.Unknown;

    /// <summary>
    /// Refusal text emitted by the model when it declines to respond
    /// (policy violation, unsafe request). Mutually exclusive with
    /// <see cref="Content"/>: when this is non-null, <see cref="Content"/>
    /// is null and <see cref="FinishReason"/> is
    /// <see cref="FinishReason.ContentFilter"/>.
    /// <para>
    /// LlamaSharp surfaces this via the <c>"refusal"</c> envelope mode
    /// (<see cref="LocalInference.LlamaSharpToolGrammar"/>). OpenAI
    /// surfaces it via <c>message.refusal</c> on Chat Completions and a
    /// <c>type: "refusal"</c> content item on the Responses API.
    /// </para>
    /// </summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// Reason a completion stopped generating. Cross-provider normalised
/// form of OpenAI <c>finish_reason</c>, Anthropic <c>stop_reason</c>,
/// and Google <c>finishReason</c>.
/// </summary>
public enum FinishReason
{
    /// <summary>Provider did not report a reason.</summary>
    Unknown = 0,
    /// <summary>Natural end of turn / stop sequence hit.</summary>
    Stop,
    /// <summary>Hit max tokens before natural stop.</summary>
    Length,
    /// <summary>Model emitted one or more tool calls.</summary>
    ToolCalls,
    /// <summary>Provider content filter halted generation.</summary>
    ContentFilter,
}

/// <summary>
/// Token counts returned by a provider for a single completion call.
/// </summary>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// A fragment of a streamed tool call. Providers emit one or more of
/// these per tool call as <c>id</c>, <c>name</c>, and <c>arguments</c>
/// become available on the wire. Multiple fragments for the same
/// <see cref="Index"/> describe the same logical call — consumers
/// should concatenate <see cref="ArgumentsFragment"/> values in order.
/// <para>
/// Mirrors the OpenAI streaming <c>tool_calls</c> delta shape: the
/// first fragment carries <see cref="Id"/> and <see cref="Name"/>,
/// subsequent fragments carry only <see cref="ArgumentsFragment"/>
/// slices of the JSON arguments blob.
/// </para>
/// </summary>
public sealed record ChatToolCallDelta(
    int Index,
    string? Id,
    string? Name,
    string? ArgumentsFragment);

/// <summary>
/// A single chunk from a streaming chat completion. Can be either a
/// text delta (<see cref="Delta"/>), a partial tool-call delta
/// (<see cref="ToolCallDelta"/>), or the final result containing the
/// accumulated tool calls and usage (<see cref="Finished"/>).
/// </summary>
public sealed class ChatStreamChunk
{
    /// <summary>Partial text token. <see langword="null"/> when this is
    /// a tool-call delta or the final chunk.</summary>
    public string? Delta { get; init; }

    /// <summary>Partial tool-call delta. <see langword="null"/> for text
    /// chunks and the final chunk.</summary>
    public ChatToolCallDelta? ToolCallDelta { get; init; }

    /// <summary>
    /// Set on the final chunk only. Contains the complete list of tool
    /// calls (if any) and the full accumulated content.
    /// </summary>
    public ChatCompletionResult? Finished { get; init; }

    public bool IsToolCallDelta => ToolCallDelta is not null;
    public bool IsFinished => Finished is not null;

    public static ChatStreamChunk Text(string delta) => new() { Delta = delta };

    public static ChatStreamChunk ToolCall(ChatToolCallDelta delta) =>
        new() { ToolCallDelta = delta };

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
