using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.OpenAICompatible;
using SharpClaw.Modules.Providers.OpenAICompatible.Clients;
using SharpClaw.Providers.Common;

namespace SharpClaw.Tests.Providers;

[TestFixture]
public sealed class DeepSeekApiClientTests
{
    [Test]
    public async Task ChatCompletionAsync_DefaultsThinkingModeDisabled()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient();

        var result = await client.ChatCompletionAsync(
            httpClient,
            "test-key",
            "deepseek-v4-flash",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")]);

        result.Content.Should().Be("ok");
        handler.LastRequestUri?.ToString().Should().Be("https://api.deepseek.com/chat/completions");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("thinking").GetProperty("type").GetString()
            .Should().Be("disabled");
    }

    [Test]
    public async Task ChatCompletionAsync_PreservesExplicitThinkingMode()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient();
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["thinking"] = JsonSerializer.SerializeToElement(new { type = "enabled" })
        };

        await client.ChatCompletionAsync(
            httpClient,
            "test-key",
            "deepseek-v4-pro",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Think this through")],
            providerParameters: providerParameters);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("thinking").GetProperty("type").GetString()
            .Should().Be("enabled");
    }

    [Test]
    public async Task ChatCompletionAsync_EnablesThinkingWhenReasoningEffortIsSet()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient();

        await client.ChatCompletionAsync(
            httpClient,
            "test-key",
            "deepseek-v4-pro",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Think this through")],
            completionParameters: new CompletionParameters { ReasoningEffort = "high" });

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("thinking").GetProperty("type").GetString()
            .Should().Be("enabled");
        doc.RootElement.GetProperty("reasoning_effort").GetString()
            .Should().Be("high");
    }

    [Test]
    public async Task ChatCompletionAsync_ReplaysReasoningContentFromHistory()
    {
        using var handler = new CaptureHandler(ReasonedCompletionResponse, """
            {
              "choices": [
                {
                  "message": { "role": "assistant", "content": "next" },
                  "finish_reason": "stop"
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient();
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["thinking"] = JsonSerializer.SerializeToElement(new { type = "enabled" })
        };

        var first = await client.ChatCompletionAsync(
            httpClient,
            "test-key",
            "deepseek-v4-pro",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Think this through")],
            providerParameters: providerParameters);

        var second = await client.ChatCompletionAsync(
            httpClient,
            "test-key",
            "deepseek-v4-pro",
            systemPrompt: null,
            [
                new ChatCompletionMessage("assistant", first.Content!)
                {
                    ProviderMetadataJson = first.ProviderMetadataJson
                },
                new ChatCompletionMessage("user", "Continue")
            ],
            providerParameters: providerParameters);

        second.Content.Should().Be("next");

        using var doc = JsonDocument.Parse(handler.RequestBodies[1]);
        var assistantTurn = doc.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "assistant");
        assistantTurn.GetProperty("reasoning_content").GetString()
            .Should().Be("final hidden reasoning");
    }

    [Test]
    public async Task ChatCompletionWithToolsAsync_ReplaysReasoningContentForThinkingToolTurns()
    {
        using var handler = new CaptureHandler(ToolCallResponse, ReasonedCompletionResponse);
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient();
        var providerParameters = new Dictionary<string, JsonElement>
        {
            ["thinking"] = JsonSerializer.SerializeToElement(new { type = "enabled" })
        };

        var first = await client.ChatCompletionWithToolsAsync(
            httpClient,
            "test-key",
            "deepseek-v4-pro",
            systemPrompt: null,
            [new ToolAwareMessage { Role = "user", Content = "Use a tool" }],
            [new ChatToolDefinition("lookup", "Lookup information", EmptyObjectSchema())],
            providerParameters: providerParameters);

        first.HasToolCalls.Should().BeTrue();
        first.ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be("lookup");
        ExtractReasoningContent(first.ProviderMetadataJson).Should().Be("hidden tool reasoning");

        var final = await client.ChatCompletionWithToolsAsync(
            httpClient,
            "test-key",
            "deepseek-v4-pro",
            systemPrompt: null,
            [
                new ToolAwareMessage { Role = "user", Content = "Use a tool" },
                ToolAwareMessage.AssistantWithToolCalls(
                    first.ToolCalls,
                    first.Content,
                    first.ProviderMetadataJson),
                ToolAwareMessage.ToolResult(first.ToolCalls[0].Id, "lookup result")
            ],
            [new ChatToolDefinition("lookup", "Lookup information", EmptyObjectSchema())],
            providerParameters: providerParameters);

        final.Content.Should().Be("ok");
        ExtractReasoningContent(final.ProviderMetadataJson).Should().Be("final hidden reasoning");

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        var assistantTurn = requestDoc.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "assistant");
        assistantTurn.GetProperty("reasoning_content").GetString()
            .Should().Be("hidden tool reasoning");
    }

    [Test]
    public async Task ChatCompletionWithToolsAsync_ReasoningEffortWithToolsEnablesThinking()
    {
        using var handler = new CaptureHandler(ToolCallResponse);
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient();

        var result = await client.ChatCompletionWithToolsAsync(
            httpClient,
            "test-key",
            "deepseek-v4-pro",
            systemPrompt: null,
            [new ToolAwareMessage { Role = "user", Content = "Use a tool" }],
            [new ChatToolDefinition("lookup", "Lookup information", EmptyObjectSchema())],
            completionParameters: new CompletionParameters { ReasoningEffort = "high" });

        result.HasToolCalls.Should().BeTrue();

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("thinking").GetProperty("type").GetString()
            .Should().Be("enabled");
        doc.RootElement.GetProperty("reasoning_effort").GetString()
            .Should().Be("high");
    }

    [Test]
    public async Task StreamChatCompletionWithToolsAsync_AccumulatesReasoningContent()
    {
        const string streamResponse = """
            data: {"choices":[{"index":0,"delta":{"reasoning_content":"hidden "}}]}

            data: {"choices":[{"index":0,"delta":{"reasoning_content":"stream reasoning"}}]}

            data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}

            data: [DONE]

            """;

        using var handler = CaptureHandler.Stream(streamResponse);
        using var httpClient = new HttpClient(handler);
        var client = new DeepSeekApiClient();

        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in client.StreamChatCompletionWithToolsAsync(
            httpClient,
            "test-key",
            "deepseek-v4-pro",
            systemPrompt: null,
            [new ToolAwareMessage { Role = "user", Content = "Think then answer" }],
            [new ChatToolDefinition("lookup", "Lookup information", EmptyObjectSchema())],
            providerParameters: new Dictionary<string, JsonElement>
            {
                ["thinking"] = JsonSerializer.SerializeToElement(new { type = "enabled" })
            }))
        {
            chunks.Add(chunk);
        }

        chunks.Where(c => c.Delta is not null)
            .Select(c => c.Delta)
            .Should().Equal("ok");

        var final = chunks.Single(c => c.IsFinished).Finished!;
        final.Content.Should().Be("ok");
        final.Usage.Should().Be(new TokenUsage(1, 2));
        ExtractReasoningContent(final.ProviderMetadataJson)
            .Should().Be("hidden stream reasoning");
    }

    [Test]
    public void ModuleRegistersDeepSeekProvider()
    {
        var services = new ServiceCollection();
        new OpenAICompatibleProvidersModule().ConfigureServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        var plugin = serviceProvider.GetServices<IProviderPlugin>()
            .Single(p => p.ProviderKey == WellKnownProviderKeys.DeepSeek);

        plugin.DisplayName.Should().Be("DeepSeek");
        plugin.OwnerModuleId.Should().Be("sharpclaw_providers_openai_compat");
        plugin.ParameterSpec.Should().BeSameAs(ProviderParameterSpecs.DeepSeek);
        plugin.CreateClient(null).Should().BeOfType<DeepSeekApiClient>();
        plugin.Capabilities.Resolve("deepseek-v4-flash")
            .Should().BeEquivalentTo([WellKnownCapabilityKeys.Chat]);
    }

    [Test]
    public void ParameterSpecMatchesDeepSeekSurface()
    {
        var spec = ProviderParameterSpecs.For(WellKnownProviderKeys.DeepSeek);

        spec.ProviderName.Should().Be("DeepSeek");
        spec.SupportsResponseFormat.Should().BeTrue();
        spec.OnlyJsonObjectResponseFormat.Should().BeTrue();
        spec.SupportsReasoningEffort.Should().BeTrue();
        spec.ValidReasoningEffortValues.Should().BeEquivalentTo(["low", "medium", "high", "xhigh", "max"]);
        spec.SupportsFrequencyPenalty.Should().BeFalse();
        spec.SupportsPresencePenalty.Should().BeFalse();
        spec.SupportsStrictTools.Should().BeFalse();
    }

    private const string ToolCallResponse = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": null,
                "reasoning_content": "hidden tool reasoning",
                "tool_calls": [
                  {
                    "id": "call_1",
                    "type": "function",
                    "function": {
                      "name": "lookup",
                      "arguments": "{}"
                    }
                  }
                ]
              },
              "finish_reason": "tool_calls"
            }
          ],
          "usage": {
            "prompt_tokens": 2,
            "completion_tokens": 3,
            "total_tokens": 5
          }
        }
        """;

    private const string ReasonedCompletionResponse = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "ok",
                "reasoning_content": "final hidden reasoning"
              },
              "finish_reason": "stop"
            }
          ],
          "usage": {
            "prompt_tokens": 1,
            "completion_tokens": 1,
            "total_tokens": 2
          }
        }
        """;

    private static JsonElement EmptyObjectSchema()
        => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object?>()
        });

    private static string? ExtractReasoningContent(string? providerMetadataJson)
    {
        providerMetadataJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(providerMetadataJson!);
        return doc.RootElement.GetProperty("reasoning_content").GetString();
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private const string CompletionResponse = """
            {
              "choices": [
                {
                  "message": { "role": "assistant", "content": "ok" },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 1,
                "completion_tokens": 1,
                "total_tokens": 2
              }
            }
            """;

        private readonly Queue<(string Body, string MediaType)> _responses;

        public CaptureHandler(params string[] responses)
            : this(responses.Length > 0
                ? responses.Select(r => (Body: r, MediaType: "application/json"))
                : [(Body: CompletionResponse, MediaType: "application/json")])
        {
        }

        public static CaptureHandler Stream(string response)
            => new([(Body: response, MediaType: "text/event-stream")]);

        private CaptureHandler(IEnumerable<(string Body, string MediaType)> responses)
        {
            _responses = new Queue<(string Body, string MediaType)>(responses);
        }

        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public List<string> RequestBodies { get; } = [];
        public int SendCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                RequestBodies.Add(LastRequestBody);
            }

            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : (Body: CompletionResponse, MediaType: "application/json");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, response.MediaType)
            };
        }
    }
}
