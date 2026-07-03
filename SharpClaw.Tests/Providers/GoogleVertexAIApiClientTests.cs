using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.Google;
using SharpClaw.Modules.Providers.Google.Clients;
using SharpClaw.Providers.Common;

namespace SharpClaw.Tests.Providers;

[TestFixture]
public sealed class GoogleVertexAIApiClientTests
{
    [Test]
    public async Task ChatCompletionAsync_UsesProjectLocationEndpointWithBearerAuth()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleVertexAIApiClient(
            "https://europe-west4-aiplatform.googleapis.com/v1/projects/test-project/locations/europe-west4",
            "test-token",
            httpClient);

        await client.ChatCompletionAsync(
            "gemini-test",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")],
            completionParameters: new CompletionParameters
            {
                PresencePenalty = 0.25f,
                FrequencyPenalty = -0.5f
            });

        handler.LastRequestUri?.ToString()
            .Should().Be(
                "https://europe-west4-aiplatform.googleapis.com/v1/projects/test-project/locations/europe-west4/publishers/google/models/gemini-test:generateContent");
        handler.LastAuthorization?.Scheme.Should().Be("Bearer");
        handler.LastAuthorization?.Parameter.Should().Be("test-token");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var generationConfig = doc.RootElement.GetProperty("generationConfig");
        generationConfig.GetProperty("presencePenalty").GetDouble()
            .Should().BeApproximately(0.25, 0.000001);
        generationConfig.GetProperty("frequencyPenalty").GetDouble()
            .Should().BeApproximately(-0.5, 0.000001);
    }

    [Test]
    public async Task ChatCompletionAsync_UsesFullyQualifiedModelWithDefaultVersionRoot()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleVertexAIApiClient(apiKey: "Bearer test-token", httpClient: httpClient);

        await client.ChatCompletionAsync(
            "projects/test-project/locations/us-central1/publishers/google/models/gemini-test",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")]);

        handler.LastRequestUri?.ToString()
            .Should().Be(
                "https://aiplatform.googleapis.com/v1/projects/test-project/locations/us-central1/publishers/google/models/gemini-test:generateContent");
        handler.LastAuthorization?.Parameter.Should().Be("test-token");
    }

    [Test]
    public async Task ChatCompletionAsync_NormalizesProviderParametersToVertexRequestShape()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleVertexAIApiClient(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/test-project/locations/us-central1",
            "test-token",
            httpClient);
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["response_mime_type"] = JsonSerializer.SerializeToElement("application/json"),
            ["generation_config"] = JsonSerializer.SerializeToElement(new
            {
                audio_timestamp = true,
                routing_config = new
                {
                    autoMode = new
                    {
                        modelRoutingPreference = "BALANCED"
                    }
                }
            }),
            ["model_armor_config"] = JsonSerializer.SerializeToElement(new
            {
                someOption = true
            }),
            ["labels"] = JsonSerializer.SerializeToElement(new
            {
                workload = "sharpclaw"
            })
        };

        await client.ChatCompletionAsync(
            "gemini-test",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")],
            providerParameters: providerParameters);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.TryGetProperty("response_mime_type", out _)
            .Should().BeFalse();
        doc.RootElement.TryGetProperty("generation_config", out _)
            .Should().BeFalse();
        doc.RootElement.TryGetProperty("model_armor_config", out _)
            .Should().BeFalse();
        doc.RootElement.GetProperty("modelArmorConfig")
            .GetProperty("someOption").GetBoolean()
            .Should().BeTrue();
        doc.RootElement.GetProperty("labels")
            .GetProperty("workload").GetString()
            .Should().Be("sharpclaw");

        var generationConfig = doc.RootElement.GetProperty("generationConfig");
        generationConfig.GetProperty("responseMimeType").GetString()
            .Should().Be("application/json");
        generationConfig.GetProperty("audioTimestamp").GetBoolean()
            .Should().BeTrue();
        generationConfig.GetProperty("routingConfig")
            .GetProperty("autoMode")
            .GetProperty("modelRoutingPreference").GetString()
            .Should().Be("BALANCED");
    }

    [Test]
    public async Task ChatCompletionWithToolsAsync_MapsToolChoiceToToolConfig()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GoogleVertexAIApiClient(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/test-project/locations/us-central1",
            "test-token",
            httpClient);

        await client.ChatCompletionWithToolsAsync(
            "gemini-test",
            systemPrompt: null,
            [new ToolAwareMessage { Role = "user", Content = "Hello" }],
            [new ChatToolDefinition("lookup", "Lookup information", EmptyObjectSchema())],
            completionParameters: new CompletionParameters
            {
                ToolChoice = ToolChoice.Required
            });

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement
            .GetProperty("toolConfig")
            .GetProperty("functionCallingConfig")
            .GetProperty("mode").GetString()
            .Should().Be("ANY");
    }

    [Test]
    public async Task ListModelIdsAsync_ListsProjectModelsFromEndpoint()
    {
        using var handler = new CaptureHandler("""
            {
              "models": [
                { "name": "projects/test-project/locations/us-central1/models/custom-model" }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new GoogleVertexAIApiClient(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/test-project/locations/us-central1",
            "test-token",
            httpClient);

        var models = await client.ListModelIdsAsync();

        handler.LastRequestUri?.ToString()
            .Should().Be("https://us-central1-aiplatform.googleapis.com/v1/projects/test-project/locations/us-central1/models");
        models.Should().Equal("custom-model");
    }

    [Test]
    public void ModuleRegistersImplementedNativeVertexProvider()
    {
        var services = new ServiceCollection();
        new GoogleProvidersModule().ConfigureServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        var plugin = serviceProvider.GetServices<IProviderPlugin>()
            .Single(p => p.ProviderKey == "google-vertex-ai");

        plugin.SupportsAutomaticEndpointDiscovery.Should().BeTrue();
        plugin.ParameterSpec.Should().BeSameAs(ProviderParameterSpecs.GoogleVertexAI);
        plugin.CreateClient(new ProviderClientOptions(
                "https://us-central1-aiplatform.googleapis.com/v1/projects/p/locations/us-central1",
                "test-token"))
            .Should().BeOfType<GoogleVertexAIApiClient>()
            .Which.SupportsNativeToolCalling.Should().BeTrue();
    }

    private static JsonElement EmptyObjectSchema()
        => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object?>()
        });

    private sealed class CaptureHandler(string? responseBody = null) : HttpMessageHandler
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
        public Uri? LastRequestUri { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody ?? CompletionResponse, Encoding.UTF8, "application/json")
            };
        }
    }
}
