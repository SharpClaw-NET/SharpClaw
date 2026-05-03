using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.Google.Clients;

/// <summary>
/// Native Google Vertex AI client (<c>generateContent</c> endpoint).
/// <para>
/// <b>Not yet implemented.</b> This stub exists to reserve the
/// <c>google-vertex-ai</c> provider key for the native
/// Vertex AI protocol. All methods throw
/// <see cref="NotSupportedException"/>.
/// Use <see cref="GoogleVertexAIOpenAiApiClient"/> instead.
/// </para>
/// </summary>
public sealed class GoogleVertexAIApiClient : IProviderApiClient
{
    private const string NotImplementedMessage =
        "Native Google Vertex AI client is not yet implemented. " +
        "Use the 'GoogleVertexAIOpenAi' provider type instead.";

    public string ProviderKey => WellKnownProviderKeys.GoogleVertexAI;
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
