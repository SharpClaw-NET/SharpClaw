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
        CancellationToken ct = default)
    {
        var resolvedKey = await ResolveApiKeyAsync(httpClient, apiKey, ct);

        var payloadMessages = new List<CompletionMessagePayload>();

        if (systemPrompt is not null)
            payloadMessages.Add(new CompletionMessagePayload("system", systemPrompt));

        foreach (var msg in messages)
            payloadMessages.Add(new CompletionMessagePayload(msg.Role, msg.Content));

        var payload = new CompletionRequest(model, payloadMessages);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
        ConfigureRequest(request);
        request.Content = JsonContent.Create(payload);

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
        CancellationToken ct = default)
    {
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
            Tools = tools.Select(t => new OaiToolDefinitionPayload
            {
                Function = new OaiFunctionDefinitionPayload
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.ParametersSchema
                }
            }).ToList()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
        ConfigureRequest(request);
        request.Content = JsonContent.Create(payload, options: OaiToolJsonOptions);

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
            ToolCalls = toolCalls
        };
    }

    private static object ConvertToOaiMessage(ToolAwareMessage msg) => msg switch
    {
        { Role: "system" } => new OaiMessage { Role = "system", Content = msg.Content! },
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
        { Role: "tool" } => new OaiToolResultMessage
        {
            ToolCallId = msg.ToolCallId!,
            Content = msg.Content ?? ""
        },
        _ => throw new ArgumentException($"Unknown message role: {msg.Role}")
    };

    public virtual async IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
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
            Tools = tools.Select(t => new OaiToolDefinitionPayload
            {
                Function = new OaiFunctionDefinitionPayload
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.ParametersSchema
                }
            }).ToList(),
            Stream = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
        ConfigureRequest(request);
        request.Content = JsonContent.Create(payload, options: OaiToolJsonOptions);

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var contentBuilder = new System.Text.StringBuilder();
        // Accumulate tool calls by index
        var toolCallAccumulator = new Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            OaiStreamChoice? choice;
            try
            {
                var chunk = JsonSerializer.Deserialize<OaiStreamResponse>(data);
                choice = chunk?.Choices?.FirstOrDefault();
            }
            catch (JsonException)
            {
                continue;
            }

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
            ToolCalls = toolCalls
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
    /// Allows subclasses to add provider-specific headers to outgoing API requests.
    /// </summary>
    protected virtual void ConfigureRequest(HttpRequestMessage request) { }

    // ── Models listing ────────────────────────────────────────────

    private sealed record ModelsListResponse(
        [property: JsonPropertyName("data")] List<ModelEntry>? Data);

    private sealed record ModelEntry(
        [property: JsonPropertyName("id")] string? Id);

    // ── Chat completion ───────────────────────────────────────────

    private sealed record CompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<CompletionMessagePayload> Messages);

    private sealed record CompletionMessagePayload(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record CompletionResponse(
        [property: JsonPropertyName("choices")] List<CompletionChoice>? Choices);

    private sealed record CompletionChoice(
        [property: JsonPropertyName("message")] CompletionMessagePayload? Message);

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
        [JsonPropertyName("content")] public string? Content { get; init; }
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
        [JsonPropertyName("content")] public required string Content { get; init; }
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
    }

    private sealed class OaiStreamResponse
    {
        [JsonPropertyName("choices")] public List<OaiStreamChoice>? Choices { get; set; }
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
}
