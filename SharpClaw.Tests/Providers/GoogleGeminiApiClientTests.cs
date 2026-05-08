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
        var client = new GoogleGeminiApiClient();
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["response_mime_type"] = JsonSerializer.SerializeToElement("application/json")
        };

        await client.ChatCompletionAsync(
            httpClient,
            "test-key",
            "gemini-test",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")],
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
        var client = new GoogleGeminiApiClient();
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
            httpClient,
            "test-key",
            "gemini-test",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")],
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
        var client = new GoogleGeminiApiClient();
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["response_mime_type"] = JsonSerializer.SerializeToElement("application/json")
        };

        await client.ChatCompletionAsync(
            httpClient,
            "test-key",
            "gemini-test",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")],
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
    public async Task ChatCompletionAsync_StillPassesUnknownNativeRootParameters()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleGeminiApiClient();
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
            httpClient,
            "test-key",
            "gemini-test",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")],
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
        var client = new GoogleGeminiApiClient();
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["generation_config"] = JsonSerializer.SerializeToElement("application/json")
        };

        var action = async () => await client.ChatCompletionAsync(
            httpClient,
            "test-key",
            "gemini-test",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")],
            providerParameters: providerParameters);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Google Gemini provider parameter 'generation_config' must be a JSON object.");
    }

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
