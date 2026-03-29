using System.Text.Json;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

public interface IProviderApiClient
{
    ProviderType ProviderType { get; }

    /// <summary>
    /// Indicates whether this provider supports native tool/function calling.
    /// When <see langword="true"/>, callers should prefer
    /// <see cref="ChatCompletionWithToolsAsync"/> over text-based tool parsing.
    /// </summary>
    bool SupportsNativeToolCalling => false;

    Task<IReadOnlyList<string>> ListModelIdsAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct = default);

    Task<ChatCompletionResult> ChatCompletionAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ChatCompletionMessage> messages,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a chat completion request with native tool definitions and
    /// returns a structured result that distinguishes text from tool calls.
    /// </summary>
    Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException(
            $"Provider '{ProviderType}' does not support native tool calling.");
    }

    /// <summary>
    /// Streams a chat completion with native tool definitions, yielding
    /// text deltas as they arrive. When the model finishes, yields a
    /// final <see cref="ChatCompletionResult"/> containing any tool calls.
    /// <para>Providers that do not support streaming should override to
    /// fall back to <see cref="ChatCompletionWithToolsAsync"/>.</para>
    /// </summary>
    IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException(
            $"Provider '{ProviderType}' does not support streaming.");
    }
}
