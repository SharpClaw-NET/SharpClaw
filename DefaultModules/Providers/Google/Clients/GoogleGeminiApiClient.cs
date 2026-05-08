using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.Google.Clients;

/// <summary>
/// Native Google Gemini client that calls the <c>generateContent</c> and
/// <c>streamGenerateContent</c> endpoints directly. Provider parameters
/// use native Gemini request fields, with generation-config values merged
/// into <c>generationConfig</c>.
/// </summary>
public sealed class GoogleGeminiApiClient : IProviderApiClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string ProviderKey => WellKnownProviderKeys.GoogleGemini;
    public bool SupportsNativeToolCalling => true;

    // ── Model listing ─────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> ListModelIdsAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct = default)
    {
        // GET /v1beta/models?key=...
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models?key={apiKey}");
        var response = await httpClient.SendAsync(request, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        var body = await response.Content.ReadFromJsonAsync<GeminiModelsListResponse>(JsonOptions, ct);
        return body?.Models?
            .Select(m => m.Name)
            .Where(n => n is not null)
            .Select(n => n!.StartsWith("models/", StringComparison.Ordinal) ? n["models/".Length..] : n)
            .Order()
            .ToList() ?? [];
    }

    // ── Simple chat completion ────────────────────────────────────

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
        var toolAwareMessages = messages
            .Select(m => new ToolAwareMessage
            {
                Role = m.Role,
                Content = m.Content,
                ImageBase64 = m.ImageBase64,
                ImageMediaType = m.ImageMediaType
            })
            .ToList();

        return await ChatCompletionWithToolsAsync(
            httpClient, apiKey, model, systemPrompt,
            toolAwareMessages, [], maxCompletionTokens,
            providerParameters, completionParameters, ct);
    }

    // ── Tool-aware chat completion ────────────────────────────────

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
        var body = BuildRequestBody(model, systemPrompt, messages, tools,
            maxCompletionTokens, providerParameters, completionParameters);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/models/{model}:generateContent");
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        var result = await response.Content.ReadFromJsonAsync<GeminiGenerateContentResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty response from Gemini API.");

        return ParseGenerateContentResponse(result);
    }

    // ── Streaming ─────────────────────────────────────────────────

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
        var body = BuildRequestBody(model, systemPrompt, messages, tools,
            maxCompletionTokens, providerParameters, completionParameters);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/models/{model}:streamGenerateContent?alt=sse");
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var contentBuilder = new StringBuilder();
        var toolCalls = new List<ChatToolCall>();
        TokenUsage? usage = null;
        string? streamFinishReason = null;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];
            if (data.Length == 0) continue;

            GeminiGenerateContentResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(data, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk is null) continue;

            // Extract usage from the final chunk
            if (chunk.UsageMetadata is { } um)
                usage = new TokenUsage(um.PromptTokenCount, um.CandidatesTokenCount);

            var candidate = chunk.Candidates?.FirstOrDefault();
            if (candidate?.FinishReason is { } fr) streamFinishReason = fr;
            if (candidate?.Content?.Parts is not { } parts) continue;

            foreach (var part in parts)
            {
                if (part.Text is { } text && text.Length > 0)
                {
                    contentBuilder.Append(text);
                    yield return ChatStreamChunk.Text(text);
                }

                if (part.FunctionCall is { } fc)
                {
                    toolCalls.Add(new ChatToolCall(
                        Guid.NewGuid().ToString(),
                        fc.Name ?? "",
                        fc.Args?.ToJsonString() ?? "{}"));
                }
            }
        }

        yield return ChatStreamChunk.Final(new ChatCompletionResult
        {
            Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
            ToolCalls = toolCalls,
            Usage = usage,
            FinishReason = MapGeminiFinishReason(streamFinishReason, toolCalls.Count > 0),
        });
    }

    // ── Request body construction ─────────────────────────────────

    /// <summary>
    /// Builds the native Gemini request body as a <see cref="JsonObject"/>,
    /// then merges provider parameters additively.
    /// </summary>
    private static JsonObject BuildRequestBody(
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters)
    {
        var body = new JsonObject();

        // ── contents ──────────────────────────────────────────────
        var contents = new JsonArray();
        foreach (var msg in messages)
        {
            if (msg.Role is "system") continue; // handled via systemInstruction

            var geminiRole = msg.Role switch
            {
                "assistant" => "model",
                _ => "user"
            };

            var parts = new JsonArray();

            // Text content
            if (msg.Content is { Length: > 0 } text)
                parts.Add(new JsonObject { ["text"] = text });

            // Image content
            if (msg.HasImage)
            {
                parts.Add(new JsonObject
                {
                    ["inlineData"] = new JsonObject
                    {
                        ["mimeType"] = msg.ImageMediaType ?? "image/png",
                        ["data"] = msg.ImageBase64
                    }
                });
            }

            // Tool call results (functionResponse)
            if (msg.Role is "tool" && msg.ToolCallId is not null)
            {
                parts.Clear();
                parts.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = msg.ToolCallId,
                        ["response"] = ParseJsonOrWrap(msg.Content ?? "")
                    }
                });
            }

            // Assistant tool calls (functionCall)
            if (msg.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var tc in calls)
                {
                    parts.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["args"] = ParseJsonNodeOrEmpty(tc.ArgumentsJson)
                        }
                    });
                }
            }

            if (parts.Count > 0)
                contents.Add(new JsonObject { ["role"] = geminiRole, ["parts"] = parts });
        }
        body["contents"] = contents;

        // ── systemInstruction ─────────────────────────────────────
        var systemText = systemPrompt;
        var systemMessages = messages.Where(m => m.Role is "system" && m.Content is not null).ToList();
        if (systemMessages.Count > 0)
        {
            var combined = systemText is not null
                ? systemText + "\n" + string.Join("\n", systemMessages.Select(m => m.Content))
                : string.Join("\n", systemMessages.Select(m => m.Content));
            systemText = combined;
        }

        if (systemText is not null)
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray
                {
                    new JsonObject { ["text"] = systemText }
                }
            };
        }

        // ── generationConfig ──────────────────────────────────────
        var genConfig = new JsonObject();
        if (maxCompletionTokens is not null)
            genConfig["maxOutputTokens"] = maxCompletionTokens.Value;

        if (completionParameters is not null)
        {
            if (completionParameters.Temperature is { } temp)
                genConfig["temperature"] = temp;
            if (completionParameters.TopP is { } topP)
                genConfig["topP"] = topP;
            if (completionParameters.TopK is { } topK)
                genConfig["topK"] = topK;
            if (completionParameters.Stop is { Length: > 0 } stop)
            {
                var arr = new JsonArray();
                foreach (var s in stop) arr.Add(s);
                genConfig["stopSequences"] = arr;
            }
            if (completionParameters.Seed is { } seed)
                genConfig["seed"] = seed;
            if (completionParameters.ResponseFormat is { } rf)
            {
                genConfig["responseMimeType"] = ExtractMimeType(rf);

                // For json_schema, extract the schema and set responseSchema
                // so the native API can enforce structured output.
                if (rf.ValueKind == JsonValueKind.Object &&
                    rf.TryGetProperty("type", out var rfType) &&
                    rfType.GetString() is "json_schema" &&
                    rf.TryGetProperty("json_schema", out var jsonSchema) &&
                    jsonSchema.TryGetProperty("schema", out var schema))
                {
                    genConfig["responseSchema"] = JsonSerializer.SerializeToNode(schema);
                }
            }
            if (completionParameters.ReasoningEffort is { } effort)
            {
                genConfig["thinkingConfig"] = new JsonObject
                {
                    ["thinkingBudget"] = MapReasoningEffort(effort)
                };
            }
        }

        // ── tools ─────────────────────────────────────────────────
        if (tools.Count > 0)
        {
            var functionDeclarations = new JsonArray();
            foreach (var tool in tools)
            {
                functionDeclarations.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonSerializer.SerializeToNode(tool.ParametersSchema)
                });
            }
            body["tools"] = new JsonArray
            {
                new JsonObject { ["functionDeclarations"] = functionDeclarations }
            };
        }

        MergeProviderParameters(body, genConfig, providerParameters);

        if (genConfig.Count > 0)
            body["generationConfig"] = genConfig;

        return body;
    }

    // ── Response parsing ──────────────────────────────────────────

    private static ChatCompletionResult ParseGenerateContentResponse(GeminiGenerateContentResponse response)
    {
        var candidate = response.Candidates?.FirstOrDefault()
            ?? throw new InvalidOperationException("No candidates in Gemini response.");

        var textParts = new List<string>();
        var toolCalls = new List<ChatToolCall>();

        if (candidate.Content?.Parts is { } parts)
        {
            foreach (var part in parts)
            {
                if (part.Text is { } text)
                    textParts.Add(text);

                if (part.FunctionCall is { } fc)
                {
                    toolCalls.Add(new ChatToolCall(
                        Guid.NewGuid().ToString(),
                        fc.Name ?? "",
                        fc.Args?.ToJsonString() ?? "{}"));
                }
            }
        }

        var usage = response.UsageMetadata is { } um
            ? new TokenUsage(um.PromptTokenCount, um.CandidatesTokenCount)
            : null;

        return new ChatCompletionResult
        {
            Content = textParts.Count > 0 ? string.Join("", textParts) : null,
            ToolCalls = toolCalls,
            Usage = usage,
            FinishReason = MapGeminiFinishReason(candidate.FinishReason, toolCalls.Count > 0),
        };
    }

    /// <summary>
    /// Maps the Gemini <c>finishReason</c> string onto the normalised
    /// <see cref="FinishReason"/>. When tool calls are present a stop
    /// finish is promoted to <see cref="FinishReason.ToolCalls"/>.
    /// </summary>
    private static FinishReason MapGeminiFinishReason(string? raw, bool hasToolCalls) => raw switch
    {
        "STOP" => hasToolCalls ? FinishReason.ToolCalls : FinishReason.Stop,
        "MAX_TOKENS" => FinishReason.Length,
        "SAFETY" => FinishReason.ContentFilter,
        "RECITATION" => FinishReason.ContentFilter,
        "PROHIBITED_CONTENT" => FinishReason.ContentFilter,
        "BLOCKLIST" => FinishReason.ContentFilter,
        "SPII" => FinishReason.ContentFilter,
        _ => hasToolCalls ? FinishReason.ToolCalls : FinishReason.Unknown,
    };

    // ── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Extracts the MIME type from a <c>response_format</c> JSON element.
    /// Handles both the OpenAI-style <c>{"type": "json_object"}</c> and
    /// direct string values like <c>"application/json"</c>.
    /// </summary>
    private static string ExtractMimeType(JsonElement responseFormat)
    {
        if (responseFormat.ValueKind == JsonValueKind.String)
            return responseFormat.GetString() ?? "text/plain";

        if (responseFormat.ValueKind == JsonValueKind.Object)
        {
            if (responseFormat.TryGetProperty("type", out var typeProp))
            {
                var type = typeProp.GetString();
                return type switch
                {
                    "json_object" or "json_schema" => "application/json",
                    _ => "text/plain"
                };
            }
        }

        return "text/plain";
    }

    /// <summary>
    /// Maps SharpClaw reasoning effort values to Gemini thinking budget tokens.
    /// Values are aligned with Google's documented mapping:
    /// <list type="bullet">
    ///   <item><c>none</c>  → 0 (disables thinking; Gemini 2.5 non-Pro only)</item>
    ///   <item><c>minimal</c> / <c>low</c> → 1 024</item>
    ///   <item><c>medium</c> → 8 192</item>
    ///   <item><c>high</c> → 24 576</item>
    /// </list>
    /// For Gemini 3.x models that prefer <c>thinkingLevel</c> over
    /// <c>thinkingBudget</c>, use <c>providerParameters</c> to pass
    /// <c>thinkingConfig.thinkingLevel</c> directly.
    /// </summary>
    private static int MapReasoningEffort(string effort)
    {
        return effort.ToLowerInvariant() switch
        {
            "none" => 0,
            "minimal" => 1024,
            "low" => 1024,
            "medium" => 8192,
            "high" => 24576,
            _ => 8192 // default to medium
        };
    }

    private static readonly HashSet<string> GenerationConfigFields = new(StringComparer.Ordinal)
    {
        "stopSequences",
        "responseMimeType",
        "responseSchema",
        "_responseJsonSchema",
        "responseJsonSchema",
        "responseModalities",
        "candidateCount",
        "maxOutputTokens",
        "temperature",
        "topP",
        "topK",
        "seed",
        "presencePenalty",
        "frequencyPenalty",
        "responseLogprobs",
        "logprobs",
        "enableEnhancedCivicAnswers",
        "speechConfig",
        "thinkingConfig",
        "imageConfig",
        "mediaResolution"
    };

    private static void MergeProviderParameters(
        JsonObject body,
        JsonObject genConfig,
        Dictionary<string, JsonElement>? providerParameters)
    {
        if (providerParameters is not { Count: > 0 })
            return;

        foreach (var (key, value) in providerParameters)
        {
            if (IsGenerationConfigWrapper(key))
            {
                MergeGenerationConfig(genConfig, key, value);
                continue;
            }

            var normalizedConfigKey = NormalizeGenerationConfigKey(key);
            if (GenerationConfigFields.Contains(normalizedConfigKey))
            {
                AddIfMissing(genConfig, normalizedConfigKey, value);
                continue;
            }

            if (body.ContainsKey(key))
                continue;

            body[key] = JsonSerializer.SerializeToNode(value);
        }
    }

    private static void MergeGenerationConfig(JsonObject genConfig, string key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Google Gemini provider parameter '{key}' must be a JSON object.");
        }

        foreach (var prop in value.EnumerateObject())
        {
            AddIfMissing(
                genConfig,
                NormalizeGenerationConfigKey(prop.Name),
                prop.Value);
        }
    }

    private static bool IsGenerationConfigWrapper(string key)
        => string.Equals(key, "generationConfig", StringComparison.Ordinal)
           || string.Equals(key, "generation_config", StringComparison.Ordinal);

    private static void AddIfMissing(JsonObject target, string key, JsonElement value)
    {
        if (target.ContainsKey(key))
            return;

        target[key] = JsonSerializer.SerializeToNode(value);
    }

    private static string NormalizeGenerationConfigKey(string key)
    {
        if (!key.Contains('_', StringComparison.Ordinal))
            return key;

        var leadingUnderscores = 0;
        while (leadingUnderscores < key.Length && key[leadingUnderscores] == '_')
            leadingUnderscores++;

        var prefix = key[..leadingUnderscores];
        var remainder = key[leadingUnderscores..];
        if (remainder.Length == 0)
            return key;

        var segments = remainder
            .Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return key;

        var builder = new StringBuilder(prefix);
        builder.Append(segments[0]);
        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            builder.Append(char.ToUpperInvariant(segment[0]));
            if (segment.Length > 1)
                builder.Append(segment[1..]);
        }

        return builder.ToString();
    }

    private static JsonNode ParseJsonOrWrap(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject { ["result"] = json };
        }
        catch (JsonException)
        {
            return new JsonObject { ["result"] = json };
        }
    }

    private static JsonNode ParseJsonNodeOrEmpty(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    // ── Gemini response DTOs ──────────────────────────────────────

    private sealed record GeminiModelsListResponse(
        [property: JsonPropertyName("models")] List<GeminiModelEntry>? Models);

    private sealed record GeminiModelEntry(
        [property: JsonPropertyName("name")] string? Name);

    private sealed record GeminiGenerateContentResponse(
        [property: JsonPropertyName("candidates")] List<GeminiCandidate>? Candidates,
        [property: JsonPropertyName("usageMetadata")] GeminiUsageMetadata? UsageMetadata);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content,
        [property: JsonPropertyName("finishReason")] string? FinishReason = null);

    private sealed record GeminiContent(
        [property: JsonPropertyName("parts")] List<GeminiPart>? Parts,
        [property: JsonPropertyName("role")] string? Role);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("functionCall")] GeminiFunctionCall? FunctionCall);

    private sealed record GeminiFunctionCall(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("args")] JsonNode? Args);

    private sealed record GeminiUsageMetadata(
        [property: JsonPropertyName("promptTokenCount")] int PromptTokenCount,
        [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount);
}
