using System.Net;
using System.Text;
using System.Text.Json;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.Google.Clients;

namespace SharpClaw.Tests.Providers;

[TestFixture]
public sealed class GoogleGeminiApiClientTests
{
    [Test]
    public async Task ChatCompletionAsync_MovesTopLevelResponseMimeTypeIntoGenerationConfig()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiApiClient("test-key", httpClient);
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["response_mime_type"] = JsonSerializer.SerializeToElement("application/json")
        };

        await client.ChatCompletionAsync(
            "gemini-test",
            systemPrompt: null,
            messages: [new ChatCompletionMessage("user", "Hello")],
            providerParameters: providerParameters);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.TryGetProperty("response_mime_type", out _)
            .Should().BeFalse();
        doc.RootElement.GetProperty("generationConfig")
            .GetProperty("responseMimeType").GetString()
            .Should().Be("application/json");
    }

    [Test]
    public async Task ChatCompletionAsync_MergesGenerationConfigAndNormalizesSnakeCaseKeys()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiApiClient("test-key", httpClient);
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["generation_config"] = JsonSerializer.SerializeToElement(new
            {
                response_mime_type = "application/json",
                candidate_count = 2,
                temperature = 1.2
            })
        };

        await client.ChatCompletionAsync(
            "gemini-test",
            systemPrompt: null,
            messages: [new ChatCompletionMessage("user", "Hello")],
            providerParameters: providerParameters,
            completionParameters: new CompletionParameters { Temperature = 0.4f });

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.TryGetProperty("generation_config", out _)
            .Should().BeFalse();

        var generationConfig = doc.RootElement.GetProperty("generationConfig");
        generationConfig.GetProperty("responseMimeType").GetString()
            .Should().Be("application/json");
        generationConfig.GetProperty("candidateCount").GetInt32()
            .Should().Be(2);
        generationConfig.GetProperty("temperature").GetDouble()
            .Should().BeApproximately(0.4, 0.000001);
    }

    [Test]
    public async Task ChatCompletionAsync_TypedResponseFormatTakesPrecedenceOverProviderParameter()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiApiClient("test-key", httpClient);
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["response_mime_type"] = JsonSerializer.SerializeToElement("application/json")
        };

        await client.ChatCompletionAsync(
            "gemini-test",
            systemPrompt: null,
            messages: [new ChatCompletionMessage("user", "Hello")],
            providerParameters: providerParameters,
            completionParameters: new CompletionParameters
            {
                ResponseFormat = JsonSerializer.SerializeToElement(new { type = "text" })
            });

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("generationConfig")
            .GetProperty("responseMimeType").GetString()
            .Should().Be("text/plain");
    }

    [Test]
    public async Task ChatCompletionAsync_MapsNativePenaltyParameters()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiApiClient("test-key", httpClient);

        await client.ChatCompletionAsync(
            "gemini-test",
            systemPrompt: null,
            messages: [new ChatCompletionMessage("user", "Hello")],
            completionParameters: new CompletionParameters
            {
                PresencePenalty = 0.25f,
                FrequencyPenalty = -0.5f
            });

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var generationConfig = doc.RootElement.GetProperty("generationConfig");
        generationConfig.GetProperty("presencePenalty").GetDouble()
            .Should().BeApproximately(0.25, 0.000001);
        generationConfig.GetProperty("frequencyPenalty").GetDouble()
            .Should().BeApproximately(-0.5, 0.000001);
    }

    [Test]
    public async Task ChatCompletionWithToolsAsync_MapsToolChoiceToToolConfig()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiApiClient("test-key", httpClient);

        await client.ChatCompletionWithToolsAsync(
            "gemini-test",
            systemPrompt: null,
            messages: [new ToolAwareMessage { Role = "user", Content = "Hello" }],
            tools: [new ChatToolDefinition("lookup", "Lookup information", EmptyObjectSchema())],
            completionParameters: new CompletionParameters
            {
                ToolChoice = ToolChoice.ForFunction("lookup")
            });

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var functionCallingConfig = doc.RootElement
            .GetProperty("toolConfig")
            .GetProperty("functionCallingConfig");

        functionCallingConfig.GetProperty("mode").GetString()
            .Should().Be("ANY");
        functionCallingConfig.GetProperty("allowedFunctionNames")
            .EnumerateArray()
            .Select(v => v.GetString())
            .Should().Equal("lookup");
    }

    [Test]
    public async Task ChatCompletionAsync_StillPassesUnknownNativeRootParameters()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiApiClient("test-key", httpClient);
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["safetySettings"] = JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    category = "HARM_CATEGORY_DANGEROUS_CONTENT",
                    threshold = "BLOCK_ONLY_HIGH"
                }
            })
        };

        await client.ChatCompletionAsync(
            "gemini-test",
            systemPrompt: null,
            messages: [new ChatCompletionMessage("user", "Hello")],
            providerParameters: providerParameters);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("safetySettings")
            .EnumerateArray()
            .Should().ContainSingle()
            .Which.GetProperty("threshold").GetString()
            .Should().Be("BLOCK_ONLY_HIGH");
    }

    [Test]
    public async Task ChatCompletionAsync_RejectsNonObjectGenerationConfigProviderParameter()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiApiClient("test-key", httpClient);
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["generation_config"] = JsonSerializer.SerializeToElement("application/json")
        };

        var action = async () => await client.ChatCompletionAsync(
            "gemini-test",
            systemPrompt: null,
            messages: [new ChatCompletionMessage("user", "Hello")],
            providerParameters: providerParameters);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Google Gemini provider parameter 'generation_config' must be a JSON object.");
    }

    private static JsonElement EmptyObjectSchema()
        => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object?>()
        });

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private const string CompletionResponse = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      { "text": "ok" }
                    ],
                    "role": "model"
                  },
                  "finishReason": "STOP"
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 1,
                "candidatesTokenCount": 1
              }
            }
            """;

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompletionResponse, Encoding.UTF8, "application/json")
            };
        }
    }
}
