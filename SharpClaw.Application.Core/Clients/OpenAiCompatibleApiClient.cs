using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Base class for providers that expose OpenAI-compatible
/// <c>GET /models</c> and <c>POST /chat/completions</c> endpoints
/// with Bearer token authentication.
/// </summary>
public abstract class OpenAiCompatibleApiClient : IProviderApiClient
{
    protected abstract string ApiEndpoint { get; }
    public abstract ProviderType ProviderType { get; }
    public virtual bool SupportsNativeToolCalling => true;

    public async Task<IReadOnlyList<string>> ListModelIdsAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct = default)
    {
        var resolvedKey = await ResolveApiKeyAsync(httpClient, apiKey, ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiEndpoint}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
        ConfigureRequest(request);

        var response = await httpClient.SendAsync(request, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        var body = await response.Content.ReadFromJsonAsync<ModelsListResponse>(ct);
        return body?.Data?
            .Select(m => m.Id)
            .Where(id => id is not null)
            .Cast<string>()
            .Order()
            .ToList() ?? [];
    }

    public async Task<string> ChatCompletionAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ChatCompletionMessage> messages,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default)
    {
        // Responses API path
        if (UseResponsesApi(model))
        {
            var sb = new System.Text.StringBuilder();
            await foreach (var chunk in StreamResponsesApiAsync(
                httpClient, apiKey, model, systemPrompt,
                messages.Select(m => new ToolAwareMessage { Role = m.Role, Content = m.Content }).ToList(),
                [], maxCompletionTokens, providerParameters, completionParameters, ct))
            {
                if (chunk.IsFinished)
                    return chunk.Finished!.Content
                        ?? throw new InvalidOperationException("No response content from provider.");
                if (chunk.Delta is not null)
                    sb.Append(chunk.Delta);
            }
            return sb.Length > 0
                ? sb.ToString()
                : throw new InvalidOperationException("No response content from provider.");
        }

        var resolvedKey = await ResolveApiKeyAsync(httpClient, apiKey, ct);

        var payloadMessages = new List<CompletionMessagePayload>();

        if (systemPrompt is not null)
            payloadMessages.Add(new CompletionMessagePayload("system", systemPrompt));

        foreach (var msg in messages)
            payloadMessages.Add(new CompletionMessagePayload(msg.Role, msg.Content));

        var payload = new CompletionRequest(model, payloadMessages)
        {
            MaxTokens = maxCompletionTokens,
            Temperature = completionParameters?.Temperature,
            TopP = completionParameters?.TopP,
            FrequencyPenalty = completionParameters?.FrequencyPenalty,
            PresencePenalty = completionParameters?.PresencePenalty,
            Stop = completionParameters?.Stop,
            Seed = completionParameters?.Seed,
            ResponseFormat = completionParameters?.ResponseFormat,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
        ConfigureRequest(request);
        request.Content = MergeProviderParameters(payload, providerParameters);

        var response = await httpClient.SendAsync(request, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        var result = await response.Content.ReadFromJsonAsync<CompletionResponse>(ct);
        return result?.Choices?.FirstOrDefault()?.Message?.Content
            ?? throw new InvalidOperationException("No response content from provider.");
    }

    public virtual async Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
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
        // Responses API path
        if (UseResponsesApi(model))
        {
            ChatCompletionResult? final = null;
            await foreach (var chunk in StreamResponsesApiAsync(
                httpClient, apiKey, model, systemPrompt, messages, tools, maxCompletionTokens, providerParameters, completionParameters, ct))
            {
                if (chunk.IsFinished)
                    final = chunk.Finished;
            }
            return final ?? throw new InvalidOperationException("No response from provider.");
        }

        var resolvedKey = await ResolveApiKeyAsync(httpClient, apiKey, ct);

        var payloadMessages = new List<object>();

        if (systemPrompt is not null)
            payloadMessages.Add(new OaiMessage { Role = "system", Content = systemPrompt });

        foreach (var msg in messages)
            payloadMessages.Add(ConvertToOaiMessage(msg));

        var payload = new OaiToolCompletionRequest
        {
            Model = model,
            Messages = payloadMessages,
            MaxTokens = maxCompletionTokens,
            ParallelToolCalls = ParallelToolCallsDefault,
            Tools = tools.Select(t => new OaiToolDefinitionPayload
            {
                Function = new OaiFunctionDefinitionPayload
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.ParametersSchema
                }
            }).ToList(),
            Temperature = completionParameters?.Temperature,
            TopP = completionParameters?.TopP,
            FrequencyPenalty = completionParameters?.FrequencyPenalty,
            PresencePenalty = completionParameters?.PresencePenalty,
            Stop = completionParameters?.Stop,
            Seed = completionParameters?.Seed,
            ResponseFormat = completionParameters?.ResponseFormat,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
        ConfigureRequest(request);
        request.Content = MergeProviderParameters(payload, providerParameters, OaiToolJsonOptions);

        var response = await httpClient.SendAsync(request, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        var result = await response.Content.ReadFromJsonAsync<OaiToolCompletionResponse>(ct);
        var choice = result?.Choices?.FirstOrDefault()
            ?? throw new InvalidOperationException("No response from provider.");

        var toolCalls = choice.Message?.ToolCalls?
            .Where(tc => tc.Function is not null)
            .Select(tc => new ChatToolCall(
                tc.Id ?? Guid.NewGuid().ToString(),
                tc.Function!.Name ?? "",
                tc.Function.Arguments ?? "{}"))
            .ToList()
            ?? [];

        return new ChatCompletionResult
        {
            Content = choice.Message?.Content,
            ToolCalls = toolCalls,
            Usage = result?.Usage is { } u
                ? new TokenUsage(u.PromptTokens, u.CompletionTokens)
                : null
        };
    }

    private static object ConvertToOaiMessage(ToolAwareMessage msg) => msg switch
    {
        { Role: "system" } => new OaiMessage { Role = "system", Content = msg.Content! },
        { Role: "user", HasImage: true } => new OaiMessage
        {
            Role = "user",
            Content = BuildMultipartContent(msg.Content!, msg.ImageBase64!, msg.ImageMediaType ?? "image/png")
        },
        { Role: "user" } => new OaiMessage { Role = "user", Content = msg.Content! },
        { Role: "assistant", ToolCalls: { Count: > 0 } calls } => new OaiAssistantToolCallMessage
        {
            Content = msg.Content,
            ToolCalls = calls.Select(tc => new OaiToolCallPayload
            {
                Id = tc.Id,
                Function = new OaiFunctionCallPayload { Name = tc.Name, Arguments = tc.ArgumentsJson }
            }).ToList()
        },
        { Role: "assistant" } => new OaiMessage { Role = "assistant", Content = msg.Content },
        { Role: "tool", HasImage: true } => new OaiToolResultMessage
        {
            ToolCallId = msg.ToolCallId!,
            Content = BuildMultipartContent(msg.Content ?? "", msg.ImageBase64!, msg.ImageMediaType ?? "image/png")
        },
        { Role: "tool" } => new OaiToolResultMessage
        {
            ToolCallId = msg.ToolCallId!,
            Content = msg.Content ?? ""
        },
        _ => throw new ArgumentException($"Unknown message role: {msg.Role}")
    };

    private static List<object> BuildMultipartContent(string text, string imageBase64, string mediaType)
    {
        var parts = new List<object>
        {
            new OaiTextContentPart { Text = text },
            new OaiImageContentPart
            {
                ImageUrl = new OaiImageUrl
                {
                    Url = $"data:{mediaType};base64,{imageBase64}"
                }
            }
        };
        return parts;
    }

    public virtual async IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Responses API path — delegate entirely
        if (UseResponsesApi(model))
        {
            await foreach (var chunk in StreamResponsesApiAsync(
                httpClient, apiKey, model, systemPrompt, messages, tools, maxCompletionTokens, providerParameters, completionParameters, ct))
            {
                yield return chunk;
            }
            yield break;
        }

        var resolvedKey = await ResolveApiKeyAsync(httpClient, apiKey, ct);

        var payloadMessages = new List<object>();
        if (systemPrompt is not null)
            payloadMessages.Add(new OaiMessage { Role = "system", Content = systemPrompt });
        foreach (var msg in messages)
            payloadMessages.Add(ConvertToOaiMessage(msg));

        var payload = new OaiStreamToolCompletionRequest
        {
            Model = model,
            Messages = payloadMessages,
            MaxTokens = maxCompletionTokens,
            ParallelToolCalls = ParallelToolCallsDefault,
            Tools = tools.Select(t => new OaiToolDefinitionPayload
            {
                Function = new OaiFunctionDefinitionPayload
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.ParametersSchema
                }
            }).ToList(),
            Stream = true,
            StreamOptions = new OaiStreamOptions { IncludeUsage = true },
            Temperature = completionParameters?.Temperature,
            TopP = completionParameters?.TopP,
            FrequencyPenalty = completionParameters?.FrequencyPenalty,
            PresencePenalty = completionParameters?.PresencePenalty,
            Stop = completionParameters?.Stop,
            Seed = completionParameters?.Seed,
            ResponseFormat = completionParameters?.ResponseFormat,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
        ConfigureRequest(request);
        request.Content = MergeProviderParameters(payload, providerParameters, OaiToolJsonOptions);

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var contentBuilder = new System.Text.StringBuilder();
        // Accumulate tool calls by index
        var toolCallAccumulator = new Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();
        TokenUsage? streamUsage = null;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            OaiStreamResponse? streamChunk;
            try
            {
                streamChunk = JsonSerializer.Deserialize<OaiStreamResponse>(data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (streamChunk is null) continue;

            // Capture usage from the final chunk (sent when stream_options.include_usage is true)
            if (streamChunk.Usage is { } su)
                streamUsage = new TokenUsage(su.PromptTokens, su.CompletionTokens);

            var choice = streamChunk.Choices?.FirstOrDefault();
            if (choice?.Delta is null) continue;

            // Accumulate text deltas
            if (choice.Delta.Content is { } textDelta && textDelta.Length > 0)
            {
                contentBuilder.Append(textDelta);
                yield return ChatStreamChunk.Text(textDelta);
            }

            // Accumulate tool call deltas
            if (choice.Delta.ToolCalls is { } tcDeltas)
            {
                foreach (var tcd in tcDeltas)
                {
                    var idx = tcd.Index ?? 0;
                    if (!toolCallAccumulator.TryGetValue(idx, out var acc))
                    {
                        acc = (tcd.Id ?? "", tcd.Function?.Name ?? "", new System.Text.StringBuilder());
                        toolCallAccumulator[idx] = acc;
                    }
                    else
                    {
                        if (tcd.Id is not null) acc.Id = tcd.Id;
                        if (tcd.Function?.Name is not null) acc.Name = tcd.Function.Name;
                    }

                    if (tcd.Function?.Arguments is { } argDelta)
                        acc.Args.Append(argDelta);

                    toolCallAccumulator[idx] = acc;
                }
            }
        }

        // Emit final chunk with accumulated result
        var toolCalls = toolCallAccumulator
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new ChatToolCall(
                string.IsNullOrEmpty(kvp.Value.Id) ? Guid.NewGuid().ToString() : kvp.Value.Id,
                kvp.Value.Name,
                kvp.Value.Args.Length > 0 ? kvp.Value.Args.ToString() : "{}"))
            .ToList();

        yield return ChatStreamChunk.Final(new ChatCompletionResult
        {
            Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
            ToolCalls = toolCalls,
            Usage = streamUsage
        });
    }

    /// <summary>
    /// Resolves the API key to use for requests. Override in subclasses that
    /// require a token exchange (e.g. GitHub Copilot OAuth → Copilot token).
    /// </summary>
    protected virtual ValueTask<string> ResolveApiKeyAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct)
        => ValueTask.FromResult(apiKey);

    /// <summary>
    /// Translates provider-native parameter names to their OpenAI-compatible
    /// equivalents before merging.  Override in subclasses whose native APIs
    /// use different parameter names (e.g. Google Gemini's
    /// <c>response_mime_type</c> → <c>response_format</c>).
    /// </summary>
    protected virtual Dictionary<string, JsonElement>? TranslateProviderParameters(
        Dictionary<string, JsonElement>? providerParameters) => providerParameters;

    /// <summary>
    /// Serializes <paramref name="payload"/> and merges any user-supplied
    /// <paramref name="providerParameters"/> into the top-level JSON object.
    /// Calls <see cref="TranslateProviderParameters"/> first, then delegates
    /// to <see cref="ProviderParameterMerger"/>.
    /// </summary>
    protected HttpContent MergeProviderParameters<T>(
        T payload,
        Dictionary<string, JsonElement>? providerParameters,
        JsonSerializerOptions? options = null)
        => ProviderParameterMerger.Merge(payload, TranslateProviderParameters(providerParameters), options);

    /// <summary>
    /// Allows subclasses to add provider-specific headers to outgoing API requests.
    /// </summary>
    protected virtual void ConfigureRequest(HttpRequestMessage request) { }

    /// <summary>
    /// Returns the value for the <c>parallel_tool_calls</c> field in tool-aware
    /// completion requests.  Default is <see langword="true"/>.  Override and
    /// return <see langword="null"/> in subclasses whose endpoints do not
    /// support this parameter (e.g. Google Gemini / Vertex AI) so it is omitted
    /// from the serialized JSON.
    /// </summary>
    protected virtual bool? ParallelToolCallsDefault => true;

    /// <summary>
    /// Returns <see langword="true"/> when the given model should use the
    /// Responses API (<c>/v1/responses</c>) instead of Chat Completions.
    /// Override in provider subclasses that support it. Default: <see langword="false"/>.
    /// </summary>
    protected virtual bool UseResponsesApi(string model) => false;

    /// <summary>
    /// Returns <see langword="true"/> when the given model must use the
    /// legacy Chat Completions API (<c>/v1/chat/completions</c>).
    /// Used by subclasses that default to the Responses API to carve
    /// out legacy model families.
    /// </summary>
    protected static bool RequiresLegacyChatCompletions(string model)
    {
        var name = model.ToLowerInvariant();
        return name.StartsWith("gpt-3.5")
            || name.StartsWith("gpt-4")
            || name.StartsWith("chatgpt-4o");
    }

    // ── Models listing ────────────────────────────────────────────

    private sealed record ModelsListResponse(
        [property: JsonPropertyName("data")] List<ModelEntry>? Data);

    private sealed record ModelEntry(
        [property: JsonPropertyName("id")] string? Id);

    // ── Chat completion ───────────────────────────────────────────

    private sealed record CompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<CompletionMessagePayload> Messages)
    {
        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; init; }

        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Temperature { get; init; }

        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? TopP { get; init; }

        [JsonPropertyName("frequency_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? FrequencyPenalty { get; init; }

        [JsonPropertyName("presence_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? PresencePenalty { get; init; }

        [JsonPropertyName("stop")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Stop { get; init; }

        [JsonPropertyName("seed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Seed { get; init; }

        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? ResponseFormat { get; init; }
    }

    private sealed record CompletionMessagePayload(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record CompletionResponse(
        [property: JsonPropertyName("choices")] List<CompletionChoice>? Choices,
        [property: JsonPropertyName("usage")] OaiUsage? Usage = null);

    private sealed record CompletionChoice(
        [property: JsonPropertyName("message")] CompletionMessagePayload? Message);

    private sealed class OaiUsage
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
        [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
    }

    // ── Tool-aware completion ─────────────────────────────────────

    private static readonly JsonSerializerOptions OaiToolJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Request

    private sealed class OaiToolCompletionRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("messages")] public required List<object> Messages { get; init; }
        [JsonPropertyName("tools")] public required List<OaiToolDefinitionPayload> Tools { get; init; }
        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; init; }
        [JsonPropertyName("parallel_tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ParallelToolCalls { get; init; }
        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Temperature { get; init; }
        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? TopP { get; init; }
        [JsonPropertyName("frequency_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? FrequencyPenalty { get; init; }
        [JsonPropertyName("presence_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? PresencePenalty { get; init; }
        [JsonPropertyName("stop")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Stop { get; init; }
        [JsonPropertyName("seed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Seed { get; init; }
        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? ResponseFormat { get; init; }
    }

    private sealed class OaiToolDefinitionPayload
    {
        [JsonPropertyName("type")] public string Type => "function";
        [JsonPropertyName("function")] public required OaiFunctionDefinitionPayload Function { get; init; }
    }

    private sealed class OaiFunctionDefinitionPayload
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("description")] public required string Description { get; init; }
        [JsonPropertyName("parameters")] public required JsonElement Parameters { get; init; }
    }

    // Messages (shared for request serialization)

    private sealed class OaiMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public object? Content { get; init; }
    }

    private sealed class OaiAssistantToolCallMessage
    {
        [JsonPropertyName("role")] public string Role => "assistant";
        [JsonPropertyName("content")] public string? Content { get; init; }
        [JsonPropertyName("tool_calls")] public required List<OaiToolCallPayload> ToolCalls { get; init; }
    }

    private sealed class OaiToolResultMessage
    {
        [JsonPropertyName("role")] public string Role => "tool";
        [JsonPropertyName("tool_call_id")] public required string ToolCallId { get; init; }
        [JsonPropertyName("content")] public required object Content { get; init; }
    }

    // Multipart content blocks (for vision / image_url payloads)

    private sealed class OaiTextContentPart
    {
        [JsonPropertyName("type")] public string Type => "text";
        [JsonPropertyName("text")] public required string Text { get; init; }
    }

    private sealed class OaiImageContentPart
    {
        [JsonPropertyName("type")] public string Type => "image_url";
        [JsonPropertyName("image_url")] public required OaiImageUrl ImageUrl { get; init; }
    }

    private sealed class OaiImageUrl
    {
        [JsonPropertyName("url")] public required string Url { get; init; }
    }

    private sealed class OaiToolCallPayload
    {
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("type")] public string Type => "function";
        [JsonPropertyName("function")] public required OaiFunctionCallPayload Function { get; init; }
    }

    private sealed class OaiFunctionCallPayload
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("arguments")] public required string Arguments { get; init; }
    }

    // Response

    private sealed class OaiToolCompletionResponse
    {
        [JsonPropertyName("choices")] public List<OaiToolCompletionChoice>? Choices { get; set; }
        [JsonPropertyName("usage")] public OaiUsage? Usage { get; set; }
    }

    private sealed class OaiToolCompletionChoice
    {
        [JsonPropertyName("message")] public OaiToolCompletionMessage? Message { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private sealed class OaiToolCompletionMessage
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<OaiResponseToolCall>? ToolCalls { get; set; }
    }

    private sealed class OaiResponseToolCall
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("function")] public OaiResponseFunctionCall? Function { get; set; }
    }

    private sealed class OaiResponseFunctionCall
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }

    // ── Streaming DTOs ────────────────────────────────────────────

    private sealed class OaiStreamToolCompletionRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("messages")] public required List<object> Messages { get; init; }
        [JsonPropertyName("tools")] public required List<OaiToolDefinitionPayload> Tools { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("stream_options")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OaiStreamOptions? StreamOptions { get; init; }
        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; init; }
        [JsonPropertyName("parallel_tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ParallelToolCalls { get; init; }
        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Temperature { get; init; }
        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? TopP { get; init; }
        [JsonPropertyName("frequency_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? FrequencyPenalty { get; init; }
        [JsonPropertyName("presence_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? PresencePenalty { get; init; }
        [JsonPropertyName("stop")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Stop { get; init; }
        [JsonPropertyName("seed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Seed { get; init; }
        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? ResponseFormat { get; init; }
    }

    private sealed class OaiStreamOptions
    {
        [JsonPropertyName("include_usage")] public bool IncludeUsage { get; init; }
    }

    private sealed class OaiStreamResponse
    {
        [JsonPropertyName("choices")] public List<OaiStreamChoice>? Choices { get; set; }
        [JsonPropertyName("usage")] public OaiUsage? Usage { get; set; }
    }

    private sealed class OaiStreamChoice
    {
        [JsonPropertyName("delta")] public OaiStreamDelta? Delta { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private sealed class OaiStreamDelta
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<OaiStreamToolCallDelta>? ToolCalls { get; set; }
    }

    private sealed class OaiStreamToolCallDelta
    {
        [JsonPropertyName("index")] public int? Index { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("function")] public OaiStreamFunctionDelta? Function { get; set; }
    }

    private sealed class OaiStreamFunctionDelta
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Responses API  (/v1/responses)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts the conversation history + tools into an OpenAI Responses API
    /// streaming request and yields <see cref="ChatStreamChunk"/> instances
    /// using the same contract as the Chat Completions streaming path.
    /// </summary>
    private async IAsyncEnumerable<ChatStreamChunk> StreamResponsesApiAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resolvedKey = await ResolveApiKeyAsync(httpClient, apiKey, ct);

        // Build input array
        var input = new List<object>();
        if (systemPrompt is not null)
            input.Add(new RespInputMessage { Role = "system", Content = systemPrompt });

        foreach (var msg in messages)
        {
            switch (msg)
            {
                case { Role: "user", HasImage: true }:
                    input.Add(new RespInputMessage
                    {
                        Role = "user",
                        Content = BuildRespMultipartContent(msg.Content!, msg.ImageBase64!, msg.ImageMediaType ?? "image/png")
                    });
                    break;
                case { Role: "user" }:
                    input.Add(new RespInputMessage { Role = "user", Content = msg.Content! });
                    break;
                case { Role: "assistant", ToolCalls: { Count: > 0 } calls }:
                    // Assistant message text (if any)
                    if (!string.IsNullOrEmpty(msg.Content))
                        input.Add(new RespInputMessage { Role = "assistant", Content = msg.Content });
                    // Each tool call as a function_call output item
                    foreach (var tc in calls)
                        input.Add(new RespFunctionCallItem
                        {
                            CallId = tc.Id,
                            Name = tc.Name,
                            Arguments = tc.ArgumentsJson
                        });
                    break;
                case { Role: "assistant" }:
                    input.Add(new RespInputMessage { Role = "assistant", Content = msg.Content });
                    break;
                case { Role: "tool", HasImage: true }:
                    // Responses API function_call_output only accepts a
                    // string, so include the text result there and follow
                    // with a user message that carries the actual image.
                    input.Add(new RespFunctionCallOutputItem
                    {
                        CallId = msg.ToolCallId!,
                        Output = msg.Content ?? ""
                    });
                    input.Add(new RespInputMessage
                    {
                        Role = "user",
                        Content = BuildRespMultipartContent(
                            "Screenshot from the tool result above:",
                            msg.ImageBase64!,
                            msg.ImageMediaType ?? "image/png")
                    });
                    break;
                case { Role: "tool" }:
                    input.Add(new RespFunctionCallOutputItem
                    {
                        CallId = msg.ToolCallId!,
                        Output = msg.Content ?? ""
                    });
                    break;
            }
        }

        // Build tools array
        var respTools = tools.Select(t => new RespToolDefinition
        {
            Name = t.Name,
            Description = t.Description,
            Parameters = t.ParametersSchema
        }).ToList();

        var payload = new RespStreamRequest
        {
            Model = model,
            Input = input,
            Tools = respTools.Count > 0 ? respTools : null,
            MaxOutputTokens = maxCompletionTokens,
            Stream = true,
            Temperature = completionParameters?.Temperature,
            TopP = completionParameters?.TopP,
            FrequencyPenalty = completionParameters?.FrequencyPenalty,
            PresencePenalty = completionParameters?.PresencePenalty,
            Stop = completionParameters?.Stop,
            Seed = completionParameters?.Seed,
            Reasoning = completionParameters?.ReasoningEffort is { } effort
                ? new RespReasoningConfig { Effort = effort } : null,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
        ConfigureRequest(request);
        request.Content = MergeProviderParameters(payload, providerParameters, OaiToolJsonOptions);

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var contentBuilder = new System.Text.StringBuilder();
        // Accumulate tool calls by call_id
        var toolCallAccumulator = new Dictionary<string, (string Name, System.Text.StringBuilder Args)>();
        var toolCallOrder = new List<string>();
        // Map output_index → call_id for providers that omit call_id from delta/done events
        var outputIndexToCallId = new Dictionary<int, string>();
        TokenUsage? respUsage = null;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            RespStreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<RespStreamEvent>(data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (evt is null) continue;

            switch (evt.Type)
            {
                case "response.output_text.delta":
                    if (evt.Delta is { Length: > 0 })
                    {
                        contentBuilder.Append(evt.Delta);
                        yield return ChatStreamChunk.Text(evt.Delta);
                    }
                    break;

                case "response.function_call_arguments.delta":
                    {
                        // Resolve call_id: use explicit call_id, or fall back to output_index mapping
                        var deltaCallId = evt.CallId
                            ?? (evt.OutputIndex.HasValue && outputIndexToCallId.TryGetValue(evt.OutputIndex.Value, out var mapped) ? mapped : null);

                        if (deltaCallId is not null && evt.Delta is not null)
                        {
                            if (!toolCallAccumulator.TryGetValue(deltaCallId, out var acc))
                            {
                                acc = (evt.Name ?? "", new System.Text.StringBuilder());
                                toolCallAccumulator[deltaCallId] = acc;
                                toolCallOrder.Add(deltaCallId);
                            }
                            acc.Args.Append(evt.Delta);
                            toolCallAccumulator[deltaCallId] = acc;
                        }
                    }
                    break;

                case "response.function_call_arguments.done":
                    {
                        var doneCallId = evt.CallId
                            ?? (evt.OutputIndex.HasValue && outputIndexToCallId.TryGetValue(evt.OutputIndex.Value, out var mapped) ? mapped : null);

                        if (doneCallId is not null)
                        {
                            if (toolCallAccumulator.TryGetValue(doneCallId, out var existing))
                            {
                                // If deltas were missed or incomplete, overwrite with final arguments
                                if (existing.Args.Length == 0 && evt.Arguments is not null)
                                    toolCallAccumulator[doneCallId] = (existing.Name, new System.Text.StringBuilder(evt.Arguments));
                            }
                            else
                            {
                                toolCallAccumulator[doneCallId] = (evt.Name ?? "", new System.Text.StringBuilder(evt.Arguments ?? "{}"));
                                toolCallOrder.Add(doneCallId);
                            }
                        }
                    }
                    break;

                case "response.output_item.added":
                    // Capture tool call name when the item first appears
                    if (evt.Item is { Type: "function_call" } item
                        && item.CallId is not null && item.Name is not null)
                    {
                        if (!toolCallAccumulator.ContainsKey(item.CallId))
                        {
                            toolCallAccumulator[item.CallId] = (item.Name, new System.Text.StringBuilder());
                            toolCallOrder.Add(item.CallId);
                        }
                        else
                        {
                            var existing = toolCallAccumulator[item.CallId];
                            toolCallAccumulator[item.CallId] = (item.Name, existing.Args);
                        }

                        // Register output_index → call_id mapping for delta/done correlation
                        if (evt.OutputIndex.HasValue)
                            outputIndexToCallId[evt.OutputIndex.Value] = item.CallId;
                    }
                    break;

                case "response.completed":
                    if (evt.Response?.Usage is { } respU)
                        respUsage = new TokenUsage(respU.InputTokens, respU.OutputTokens);
                    break;
            }
        }

        // Emit final chunk
        var toolCalls = toolCallOrder
            .Where(id => toolCallAccumulator.ContainsKey(id))
            .Select(id =>
            {
                var (name, args) = toolCallAccumulator[id];
                return new ChatToolCall(id, name, args.Length > 0 ? args.ToString() : "{}");
            })
            .ToList();

        yield return ChatStreamChunk.Final(new ChatCompletionResult
        {
            Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
            ToolCalls = toolCalls,
            Usage = respUsage
        });
    }

    /// <summary>
    /// Builds multipart content for the Responses API which uses
    /// <c>input_text</c> / <c>input_image</c> content part types
    /// (unlike Chat Completions which uses <c>text</c> / <c>image_url</c>).
    /// </summary>
    private static List<object> BuildRespMultipartContent(string text, string imageBase64, string mediaType)
    {
        return
        [
            new RespTextContentPart { Text = text },
            new RespImageContentPart { ImageUrl = $"data:{mediaType};base64,{imageBase64}" }
        ];
    }

    // ── Responses API DTOs ────────────────────────────────────────

    private sealed class RespInputMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public object? Content { get; init; }
    }

    private sealed class RespTextContentPart
    {
        [JsonPropertyName("type")] public string Type => "input_text";
        [JsonPropertyName("text")] public required string Text { get; init; }
    }

    private sealed class RespImageContentPart
    {
        [JsonPropertyName("type")] public string Type => "input_image";
        [JsonPropertyName("image_url")] public required string ImageUrl { get; init; }
    }

    private sealed class RespFunctionCallItem
    {
        [JsonPropertyName("type")] public string Type => "function_call";
        [JsonPropertyName("call_id")] public required string CallId { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("arguments")] public required string Arguments { get; init; }
    }

    private sealed class RespFunctionCallOutputItem
    {
        [JsonPropertyName("type")] public string Type => "function_call_output";
        [JsonPropertyName("call_id")] public required string CallId { get; init; }
        [JsonPropertyName("output")] public required string Output { get; init; }
    }

    private sealed class RespToolDefinition
    {
        [JsonPropertyName("type")] public string Type => "function";
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("description")] public required string Description { get; init; }
        [JsonPropertyName("parameters")] public required JsonElement Parameters { get; init; }
    }

    private sealed class RespStreamRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("input")] public required List<object> Input { get; init; }
        [JsonPropertyName("tools")] public List<RespToolDefinition>? Tools { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("max_output_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxOutputTokens { get; init; }
        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Temperature { get; init; }
        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? TopP { get; init; }
        [JsonPropertyName("frequency_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? FrequencyPenalty { get; init; }
        [JsonPropertyName("presence_penalty")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? PresencePenalty { get; init; }
        [JsonPropertyName("stop")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Stop { get; init; }
        [JsonPropertyName("seed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Seed { get; init; }
        [JsonPropertyName("reasoning")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RespReasoningConfig? Reasoning { get; init; }
    }

    private sealed class RespReasoningConfig
    {
        [JsonPropertyName("effort")] public required string Effort { get; init; }
    }

    // Streaming event — covers the subset of fields we care about.
    private sealed class RespStreamEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("delta")] public string? Delta { get; set; }
        // Function call fields
        [JsonPropertyName("call_id")] public string? CallId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
        [JsonPropertyName("output_index")] public int? OutputIndex { get; set; }
        // output_item.added carries an item object
        [JsonPropertyName("item")] public RespOutputItem? Item { get; set; }
        // response.completed carries the full response object
        [JsonPropertyName("response")] public RespCompletedResponse? Response { get; set; }
    }

    private sealed class RespCompletedResponse
    {
        [JsonPropertyName("usage")] public RespUsage? Usage { get; set; }
    }

    private sealed class RespUsage
    {
        [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    }

    private sealed class RespOutputItem
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("call_id")] public string? CallId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
