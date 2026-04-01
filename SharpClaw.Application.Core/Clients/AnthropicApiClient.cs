using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

public sealed class AnthropicApiClient : IProviderApiClient
{
    private const string ApiEndpoint = "https://api.anthropic.com/v1";
    private const string ApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ProviderType ProviderType => ProviderType.Anthropic;
    public bool SupportsNativeToolCalling => true;

    public async Task<IReadOnlyList<string>> ListModelIdsAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiEndpoint}/models");
        AddAuthHeaders(request, apiKey);

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

    public async Task<ChatCompletionResult> ChatCompletionAsync(
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
        var payload = new MessagesRequest
        {
            Model = model,
            MaxTokens = maxCompletionTokens ?? 4096,
            System = systemPrompt,
            Messages = messages
                .Select(m => new MessagePayload(m.Role, m.Content))
                .ToList(),
            Temperature = completionParameters?.Temperature,
            TopP = completionParameters?.TopP,
            TopK = completionParameters?.TopK,
            StopSequences = completionParameters?.Stop,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/messages");
        AddAuthHeaders(request, apiKey);
        request.Content = ProviderParameterMerger.Merge(payload, providerParameters, WriteOptions);

        var response = await httpClient.SendAsync(request, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        var result = await response.Content.ReadFromJsonAsync<MessagesResponse>(ct);
        var content = result?.Content?.FirstOrDefault(c => c.Type == "text")?.Text
            ?? throw new InvalidOperationException("No response content from Anthropic.");

        return new ChatCompletionResult
        {
            Content = content,
            Usage = result?.Usage is { } u
                ? new TokenUsage(u.InputTokens, u.OutputTokens)
                : null
        };
    }

    private static void AddAuthHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
    }

    // ── Models listing ────────────────────────────────────────────

    private sealed record ModelsListResponse(
        [property: JsonPropertyName("data")] List<ModelEntry>? Data);

    private sealed record ModelEntry(
        [property: JsonPropertyName("id")] string? Id);

    // ── Messages (chat completion) ────────────────────────────────

    private sealed class MessagesRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("messages")] public required List<MessagePayload> Messages { get; init; }
        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Temperature { get; init; }
        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? TopP { get; init; }
        [JsonPropertyName("top_k")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? TopK { get; init; }
        [JsonPropertyName("stop_sequences")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? StopSequences { get; init; }
    }

    private sealed record MessagePayload(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record MessagesResponse(
        [property: JsonPropertyName("content")] List<ContentBlock>? Content,
        [property: JsonPropertyName("usage")] AntUsage? Usage = null);

    private sealed class AntUsage
    {
        [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    }

    private sealed record ContentBlock(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("text")] string? Text);

    // ── Tool-aware completion ─────────────────────────────────────

    public async Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
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
        var payload = new AntToolCompletionRequest
        {
            Model = model,
            MaxTokens = maxCompletionTokens ?? 4096,
            System = systemPrompt,
            Messages = ConvertToAnthropicMessages(messages),
            Tools = tools.Select(t => new AntToolDefinitionPayload
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.ParametersSchema
            }).ToList(),
            Temperature = completionParameters?.Temperature,
            TopP = completionParameters?.TopP,
            TopK = completionParameters?.TopK,
            StopSequences = completionParameters?.Stop,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/messages");
        AddAuthHeaders(request, apiKey);
        request.Content = ProviderParameterMerger.Merge(payload, providerParameters, WriteOptions);

        var response = await httpClient.SendAsync(request, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        var result = await response.Content.ReadFromJsonAsync<AntToolCompletionResponse>(ct);

        var content = result?.Content?
            .Where(b => b.Type == "text")
            .Select(b => b.Text)
            .FirstOrDefault();

        var toolCalls = result?.Content?
            .Where(b => b.Type == "tool_use" && b.Id is not null && b.Name is not null)
            .Select(b => new ChatToolCall(
                b.Id!,
                b.Name!,
                b.Input?.GetRawText() ?? "{}"))
            .ToList()
            ?? [];

        return new ChatCompletionResult
        {
            Content = content,
            ToolCalls = toolCalls
        };
    }

    /// <summary>
    /// Converts <see cref="ToolAwareMessage"/> history into Anthropic’s
    /// message format.  Consecutive <c>tool</c>-role messages are merged
    /// into a single <c>user</c> message with <c>tool_result</c> content
    /// blocks (Anthropic has no <c>tool</c> role).
    /// </summary>
    private static List<object> ConvertToAnthropicMessages(IReadOnlyList<ToolAwareMessage> messages)
    {
        var result = new List<object>();
        var i = 0;

        while (i < messages.Count)
        {
            var msg = messages[i];
            switch (msg.Role)
            {
                case "system":
                    i++;
                    break; // handled via the separate System field

                case "user" when msg.HasImage:
                {
                    var blocks = new List<object>
                    {
                        new AntTextBlock { Text = msg.Content! },
                        new AntImageBlock
                        {
                            Source = new AntImageSource
                            {
                                MediaType = msg.ImageMediaType ?? "image/png",
                                Data = msg.ImageBase64!
                            }
                        }
                    };
                    result.Add(new AntBlockMessage { Role = "user", Content = blocks });
                    i++;
                    break;
                }

                case "user":
                    result.Add(new AntStringMessage { Role = "user", Content = msg.Content! });
                    i++;
                    break;

                case "assistant" when msg.ToolCalls is { Count: > 0 } calls:
                {
                    var blocks = new List<object>();
                    if (!string.IsNullOrEmpty(msg.Content))
                        blocks.Add(new AntTextBlock { Text = msg.Content });
                    foreach (var tc in calls)
                    {
                        blocks.Add(new AntToolUseBlock
                        {
                            Id = tc.Id,
                            Name = tc.Name,
                            Input = JsonDocument.Parse(tc.ArgumentsJson).RootElement.Clone()
                        });
                    }
                    result.Add(new AntBlockMessage { Role = "assistant", Content = blocks });
                    i++;
                    break;
                }

                case "assistant":
                    result.Add(new AntStringMessage { Role = "assistant", Content = msg.Content ?? "" });
                    i++;
                    break;

                case "tool":
                {
                    var toolResults = new List<object>();
                    while (i < messages.Count && messages[i].Role == "tool")
                    {
                        var toolMsg = messages[i];
                        if (toolMsg.HasImage)
                        {
                            // Multipart tool result: text + image
                            toolResults.Add(new AntToolResultBlock
                            {
                                ToolUseId = toolMsg.ToolCallId!,
                                Content = new List<object>
                                {
                                    new AntTextBlock { Text = toolMsg.Content ?? "" },
                                    new AntImageBlock
                                    {
                                        Source = new AntImageSource
                                        {
                                            MediaType = toolMsg.ImageMediaType ?? "image/png",
                                            Data = toolMsg.ImageBase64!
                                        }
                                    }
                                }
                            });
                        }
                        else
                        {
                            toolResults.Add(new AntToolResultBlock
                            {
                                ToolUseId = toolMsg.ToolCallId!,
                                Content = toolMsg.Content ?? ""
                            });
                        }
                        i++;
                    }
                    result.Add(new AntBlockMessage { Role = "user", Content = toolResults });
                    break;
                }

                default:
                    i++;
                    break;
            }
        }

        return result;
    }

    // Request DTOs

    private sealed class AntToolCompletionRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("messages")] public required List<object> Messages { get; init; }
        [JsonPropertyName("tools")] public required List<AntToolDefinitionPayload> Tools { get; init; }
        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Temperature { get; init; }
        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? TopP { get; init; }
        [JsonPropertyName("top_k")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? TopK { get; init; }
        [JsonPropertyName("stop_sequences")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? StopSequences { get; init; }
    }

    private sealed class AntToolDefinitionPayload
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("description")] public required string Description { get; init; }
        [JsonPropertyName("input_schema")] public required JsonElement InputSchema { get; init; }
    }

    // Message payloads

    private sealed class AntStringMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
    }

    private sealed class AntBlockMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required List<object> Content { get; init; }
    }

    private sealed class AntTextBlock
    {
        [JsonPropertyName("type")] public string Type => "text";
        [JsonPropertyName("text")] public required string Text { get; init; }
    }

    private sealed class AntToolUseBlock
    {
        [JsonPropertyName("type")] public string Type => "tool_use";
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("input")] public required JsonElement Input { get; init; }
    }

    private sealed class AntToolResultBlock
    {
        [JsonPropertyName("type")] public string Type => "tool_result";
        [JsonPropertyName("tool_use_id")] public required string ToolUseId { get; init; }
        [JsonPropertyName("content")] public required object Content { get; init; }
    }

    private sealed class AntImageBlock
    {
        [JsonPropertyName("type")] public string Type => "image";
        [JsonPropertyName("source")] public required AntImageSource Source { get; init; }
    }

    private sealed class AntImageSource
    {
        [JsonPropertyName("type")] public string Type => "base64";
        [JsonPropertyName("media_type")] public required string MediaType { get; init; }
        [JsonPropertyName("data")] public required string Data { get; init; }
    }

    // Response DTOs

    private sealed class AntToolCompletionResponse
    {
        [JsonPropertyName("content")] public List<AntResponseBlock>? Content { get; set; }
        [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    }

    private sealed class AntResponseBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("input")] public JsonElement? Input { get; set; }
    }

    // ── Streaming ─────────────────────────────────────────────

    public async IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
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
        var payload = new AntStreamRequest
        {
            Model = model,
            MaxTokens = maxCompletionTokens ?? 4096,
            System = systemPrompt,
            Messages = ConvertToAnthropicMessages(messages),
            Tools = tools.Select(t => new AntToolDefinitionPayload
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.ParametersSchema
            }).ToList(),
            Stream = true,
            Temperature = completionParameters?.Temperature,
            TopP = completionParameters?.TopP,
            TopK = completionParameters?.TopK,
            StopSequences = completionParameters?.Stop,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/messages");
        AddAuthHeaders(request, apiKey);
        request.Content = ProviderParameterMerger.Merge(payload, providerParameters, WriteOptions);

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var contentBuilder = new System.Text.StringBuilder();
        var toolCalls = new List<(string Id, string Name, System.Text.StringBuilder Args)>();
        string? currentToolId = null;
        string? currentToolName = null;
        System.Text.StringBuilder? currentToolArgs = null;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];

            AntStreamEvent? evt;
            try { evt = JsonSerializer.Deserialize<AntStreamEvent>(data); }
            catch (JsonException) { continue; }
            if (evt is null) continue;

            switch (evt.Type)
            {
                case "content_block_start" when evt.ContentBlock?.Type == "tool_use":
                    currentToolId = evt.ContentBlock.Id ?? "";
                    currentToolName = evt.ContentBlock.Name ?? "";
                    currentToolArgs = new System.Text.StringBuilder();
                    break;

                case "content_block_delta" when evt.Delta?.Type == "text_delta"
                                             && evt.Delta.Text is { } textDelta:
                    contentBuilder.Append(textDelta);
                    yield return ChatStreamChunk.Text(textDelta);
                    break;

                case "content_block_delta" when evt.Delta?.Type == "input_json_delta"
                                             && evt.Delta.PartialJson is { } argDelta:
                    currentToolArgs?.Append(argDelta);
                    break;

                case "content_block_stop" when currentToolId is not null:
                    toolCalls.Add((
                        currentToolId,
                        currentToolName ?? "",
                        currentToolArgs ?? new System.Text.StringBuilder()));
                    currentToolId = null;
                    currentToolName = null;
                    currentToolArgs = null;
                    break;

                case "message_stop":
                    goto done;
            }
        }

        done:
        var resultToolCalls = toolCalls
            .Select(tc => new ChatToolCall(
                tc.Id, tc.Name,
                tc.Args.Length > 0 ? tc.Args.ToString() : "{}"))
            .ToList();

        yield return ChatStreamChunk.Final(new ChatCompletionResult
        {
            Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
            ToolCalls = resultToolCalls
        });
    }

    // Streaming request

    private sealed class AntStreamRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("messages")] public required List<object> Messages { get; init; }
        [JsonPropertyName("tools")] public required List<AntToolDefinitionPayload> Tools { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Temperature { get; init; }
        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? TopP { get; init; }
        [JsonPropertyName("top_k")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? TopK { get; init; }
        [JsonPropertyName("stop_sequences")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? StopSequences { get; init; }
    }

    // Streaming event DTOs

    private sealed class AntStreamEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("delta")] public AntStreamDelta? Delta { get; set; }
        [JsonPropertyName("content_block")] public AntStreamContentBlock? ContentBlock { get; set; }
    }

    private sealed class AntStreamDelta
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("partial_json")] public string? PartialJson { get; set; }
    }

    private sealed class AntStreamContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
