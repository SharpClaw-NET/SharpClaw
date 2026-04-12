using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Native Google Vertex AI client (<c>generateContent</c> endpoint).
/// <para>
/// <b>Not yet implemented.</b> This stub exists to reserve the
/// <see cref="ProviderType.GoogleVertexAI"/> enum value for the native
/// Vertex AI protocol. All methods throw
/// <see cref="NotSupportedException"/>.
/// Use <see cref="GoogleVertexAIOpenAiApiClient"/>
/// (<see cref="ProviderType.GoogleVertexAIOpenAi"/>) instead.
/// </para>
/// </summary>
public sealed class GoogleVertexAIApiClient : IProviderApiClient
{
    private const string NotImplementedMessage =
        "Native Google Vertex AI client is not yet implemented. " +
        "Use the 'GoogleVertexAIOpenAi' provider type instead.";

    public ProviderType ProviderType => ProviderType.GoogleVertexAI;
    public bool SupportsNativeToolCalling => false;

    public Task<IReadOnlyList<string>> ListModelIdsAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct = default)
        => throw new NotSupportedException(NotImplementedMessage);

    public Task<ChatCompletionResult> ChatCompletionAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ChatCompletionMessage> messages,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default)
        => throw new NotSupportedException(NotImplementedMessage);

    public Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
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
        => throw new NotSupportedException(NotImplementedMessage);

    public IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
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
        => throw new NotSupportedException(NotImplementedMessage);
}
