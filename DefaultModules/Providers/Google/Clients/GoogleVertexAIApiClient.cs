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
/// Native Google Vertex AI client that calls the Gemini
/// <c>generateContent</c> and <c>streamGenerateContent</c> endpoints.
/// </summary>
public sealed class GoogleVertexAIApiClient : IProviderApiClient
{
    private const string DefaultApiEndpoint = "https://aiplatform.googleapis.com/v1";

    private readonly string _apiEndpoint;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public GoogleVertexAIApiClient(string? apiEndpoint = null)
    {
        _apiEndpoint = NormalizeApiEndpoint(apiEndpoint);
    }

    public string ProviderKey => WellKnownProviderKeys.GoogleVertexAI;
    public bool SupportsNativeToolCalling => true;

    public async Task<IReadOnlyList<string>> ListModelIdsAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct = default)
    {
        var projectLocationEndpoint = GetProjectLocationEndpoint()
            ?? throw new InvalidOperationException(
                "Native Google Vertex AI model listing requires an API endpoint ending in " +
                "'/v1/projects/{project}/locations/{location}'.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{projectLocationEndpoint}/models");
        AddAuthorization(request, apiKey);

        var response = await httpClient.SendAsync(request, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        var body = await response.Content.ReadFromJsonAsync<VertexModelsListResponse>(JsonOptions, ct);
        return body?.Models?
            .Select(m => m.Name)
            .Where(n => n is not null)
            .Select(StripModelResourcePrefix)
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
        var body = BuildRequestBody(systemPrompt, messages, tools,
            maxCompletionTokens, providerParameters, completionParameters);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            BuildGenerateContentUri(model, stream: false));
        AddAuthorization(request, apiKey);
        request.Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request, ct);
        await response.EnsureSuccessOrThrowAsync(ct);

        var result = await response.Content.ReadFromJsonAsync<VertexGenerateContentResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty response from Vertex AI API.");

        return ParseGenerateContentResponse(result);
    }

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
        var body = BuildRequestBody(systemPrompt, messages, tools,
            maxCompletionTokens, providerParameters, completionParameters);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            BuildGenerateContentUri(model, stream: true));
        AddAuthorization(request, apiKey);
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

            VertexGenerateContentResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<VertexGenerateContentResponse>(data, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk is null) continue;

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
            FinishReason = MapVertexFinishReason(streamFinishReason, toolCalls.Count > 0),
        });
    }

    private JsonObject BuildRequestBody(
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters)
    {
        var body = new JsonObject();

        var contents = new JsonArray();
        foreach (var msg in messages)
        {
            if (msg.Role is "system") continue;

            var vertexRole = msg.Role switch
            {
                "assistant" => "model",
                _ => "user"
            };

            var parts = new JsonArray();
            if (msg.Content is { Length: > 0 } text)
                parts.Add(new JsonObject { ["text"] = text });

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
                contents.Add(new JsonObject { ["role"] = vertexRole, ["parts"] = parts });
        }
        body["contents"] = contents;

        var systemText = systemPrompt;
        var systemMessages = messages.Where(m => m.Role is "system" && m.Content is not null).ToList();
        if (systemMessages.Count > 0)
        {
            systemText = systemText is not null
                ? systemText + "\n" + string.Join("\n", systemMessages.Select(m => m.Content))
                : string.Join("\n", systemMessages.Select(m => m.Content));
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
            if (completionParameters.PresencePenalty is { } presencePenalty)
                genConfig["presencePenalty"] = presencePenalty;
            if (completionParameters.FrequencyPenalty is { } frequencyPenalty)
                genConfig["frequencyPenalty"] = frequencyPenalty;
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

            ApplyToolChoice(body, completionParameters?.ToolChoice);
        }

        MergeProviderParameters(body, genConfig, providerParameters);

        if (genConfig.Count > 0)
            body["generationConfig"] = genConfig;

        return body;
    }

    private Uri BuildGenerateContentUri(string model, bool stream)
    {
        var method = stream ? "streamGenerateContent?alt=sse" : "generateContent";
        var endpoint = _apiEndpoint.TrimEnd('/');

        if (model.StartsWith("projects/", StringComparison.Ordinal))
            return new Uri($"{GetVersionRoot(endpoint)}/{model}:{method}");

        if (endpoint.EndsWith("/models", StringComparison.Ordinal))
            return new Uri($"{endpoint}/{Uri.EscapeDataString(model)}:{method}");

        if (GetProjectLocationEndpoint() is { } projectLocationEndpoint)
            return new Uri($"{projectLocationEndpoint}/publishers/google/models/{Uri.EscapeDataString(model)}:{method}");

        throw new InvalidOperationException(
            "Native Google Vertex AI requires either a fully qualified model name " +
            "('projects/{project}/locations/{location}/publishers/google/models/{model}') " +
            "or an API endpoint ending in '/v1/projects/{project}/locations/{location}'.");
    }

    private string? GetProjectLocationEndpoint()
    {
        var endpoint = _apiEndpoint.TrimEnd('/');
        var publisherIndex = endpoint.IndexOf("/publishers/", StringComparison.Ordinal);
        if (publisherIndex > 0)
            endpoint = endpoint[..publisherIndex];

        if (endpoint.EndsWith("/models", StringComparison.Ordinal))
        {
            var modelsIndex = endpoint.LastIndexOf("/publishers/google/models", StringComparison.Ordinal);
            if (modelsIndex > 0)
                endpoint = endpoint[..modelsIndex];
        }

        return endpoint.Contains("/projects/", StringComparison.Ordinal)
               && endpoint.Contains("/locations/", StringComparison.Ordinal)
            ? endpoint
            : null;
    }

    private static string GetVersionRoot(string endpoint)
    {
        var v1Index = endpoint.IndexOf("/v1", StringComparison.Ordinal);
        if (v1Index < 0)
            return endpoint.TrimEnd('/') + "/v1";

        var afterVersion = endpoint.IndexOf('/', v1Index + 1);
        return afterVersion < 0
            ? endpoint
            : endpoint[..afterVersion];
    }

    private static string NormalizeApiEndpoint(string? apiEndpoint)
    {
        if (string.IsNullOrWhiteSpace(apiEndpoint))
            return DefaultApiEndpoint;

        var endpoint = apiEndpoint.Trim().TrimEnd('/');
        return endpoint.Contains("/v1", StringComparison.Ordinal)
            ? endpoint
            : endpoint + "/v1";
    }

    private static void AddAuthorization(HttpRequestMessage request, string apiKey)
    {
        var token = apiKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? apiKey["Bearer ".Length..].Trim()
            : apiKey.Trim();

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static ChatCompletionResult ParseGenerateContentResponse(VertexGenerateContentResponse response)
    {
        var candidate = response.Candidates?.FirstOrDefault()
            ?? throw new InvalidOperationException("No candidates in Vertex AI response.");

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
            FinishReason = MapVertexFinishReason(candidate.FinishReason, toolCalls.Count > 0),
        };
    }

    private static FinishReason MapVertexFinishReason(string? raw, bool hasToolCalls) => raw switch
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

    private static string ExtractMimeType(JsonElement responseFormat)
    {
        if (responseFormat.ValueKind == JsonValueKind.String)
            return responseFormat.GetString() ?? "text/plain";

        if (responseFormat.ValueKind == JsonValueKind.Object &&
            responseFormat.TryGetProperty("type", out var typeProp))
        {
            return typeProp.GetString() switch
            {
                "json_object" or "json_schema" => "application/json",
                _ => "text/plain"
            };
        }

        return "text/plain";
    }

    private static int MapReasoningEffort(string effort)
        => effort.ToLowerInvariant() switch
        {
            "none" => 0,
            "minimal" => 1024,
            "low" => 1024,
            "medium" => 8192,
            "high" => 24576,
            _ => 8192
        };

    private static readonly HashSet<string> GenerationConfigFields = new(StringComparer.Ordinal)
    {
        "stopSequences",
        "responseMimeType",
        "responseModalities",
        "thinkingConfig",
        "temperature",
        "topP",
        "topK",
        "candidateCount",
        "maxOutputTokens",
        "responseLogprobs",
        "logprobs",
        "presencePenalty",
        "frequencyPenalty",
        "seed",
        "responseSchema",
        "responseJsonSchema",
        "routingConfig",
        "audioTimestamp",
        "mediaResolution",
        "speechConfig",
        "enableAffectiveDialog",
        "imageConfig"
    };

    private static readonly HashSet<string> RootRequestFields = new(StringComparer.Ordinal)
    {
        "cachedContent",
        "contents",
        "generationConfig",
        "labels",
        "modelArmorConfig",
        "safetySettings",
        "systemInstruction",
        "toolConfig",
        "tools"
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

            var normalizedConfigKey = NormalizeSnakeCaseKey(key);
            if (GenerationConfigFields.Contains(normalizedConfigKey))
            {
                AddIfMissing(genConfig, normalizedConfigKey, value);
                continue;
            }

            var normalizedRootKey = NormalizeRootRequestKey(key);
            if (body.ContainsKey(normalizedRootKey))
                continue;

            body[normalizedRootKey] = JsonSerializer.SerializeToNode(value);
        }
    }

    private static void MergeGenerationConfig(JsonObject genConfig, string key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Google Vertex AI provider parameter '{key}' must be a JSON object.");
        }

        foreach (var prop in value.EnumerateObject())
        {
            AddIfMissing(
                genConfig,
                NormalizeSnakeCaseKey(prop.Name),
                prop.Value);
        }
    }

    private static bool IsGenerationConfigWrapper(string key)
        => string.Equals(key, "generationConfig", StringComparison.Ordinal)
           || string.Equals(key, "generation_config", StringComparison.Ordinal);

    private static string NormalizeRootRequestKey(string key)
    {
        var normalized = NormalizeSnakeCaseKey(key);
        return RootRequestFields.Contains(normalized) ? normalized : key;
    }

    private static void ApplyToolChoice(JsonObject body, ToolChoice? toolChoice)
    {
        if (toolChoice is null || toolChoice.Mode is ToolChoiceMode.Auto)
            return;

        var functionCallingConfig = new JsonObject();
        switch (toolChoice.Mode)
        {
            case ToolChoiceMode.None:
                functionCallingConfig["mode"] = "NONE";
                break;
            case ToolChoiceMode.Required:
                functionCallingConfig["mode"] = "ANY";
                break;
            case ToolChoiceMode.Named:
                if (string.IsNullOrWhiteSpace(toolChoice.NamedFunction))
                    throw new InvalidOperationException(
                        "Google Vertex AI named tool choice requires a function name.");

                functionCallingConfig["mode"] = "ANY";
                functionCallingConfig["allowedFunctionNames"] = new JsonArray
                {
                    toolChoice.NamedFunction
                };
                break;
            case ToolChoiceMode.Auto:
            default:
                return;
        }

        body["toolConfig"] = new JsonObject
        {
            ["functionCallingConfig"] = functionCallingConfig
        };
    }

    private static void AddIfMissing(JsonObject target, string key, JsonElement value)
    {
        if (target.ContainsKey(key))
            return;

        target[key] = JsonSerializer.SerializeToNode(value);
    }

    private static string NormalizeSnakeCaseKey(string key)
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

    private static string StripModelResourcePrefix(string? name)
    {
        if (name is null)
            return string.Empty;

        const string marker = "/models/";
        var markerIndex = name.LastIndexOf(marker, StringComparison.Ordinal);
        return markerIndex >= 0
            ? name[(markerIndex + marker.Length)..]
            : name;
    }

    private sealed record VertexModelsListResponse(
        [property: JsonPropertyName("models")] List<VertexModelEntry>? Models);

    private sealed record VertexModelEntry(
        [property: JsonPropertyName("name")] string? Name);

    private sealed record VertexGenerateContentResponse(
        [property: JsonPropertyName("candidates")] List<VertexCandidate>? Candidates,
        [property: JsonPropertyName("usageMetadata")] VertexUsageMetadata? UsageMetadata);

    private sealed record VertexCandidate(
        [property: JsonPropertyName("content")] VertexContent? Content,
        [property: JsonPropertyName("finishReason")] string? FinishReason = null);

    private sealed record VertexContent(
        [property: JsonPropertyName("parts")] List<VertexPart>? Parts,
        [property: JsonPropertyName("role")] string? Role);

    private sealed record VertexPart(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("functionCall")] VertexFunctionCall? FunctionCall);

    private sealed record VertexFunctionCall(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("args")] JsonNode? Args);

    private sealed record VertexUsageMetadata(
        [property: JsonPropertyName("promptTokenCount")] int PromptTokenCount,
        [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount);
}
