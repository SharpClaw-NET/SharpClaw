using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Providers;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.OpenAICompatible;
using SharpClaw.Modules.Providers.OpenAICompatible.Clients;
using SharpClaw.Providers.Common;

namespace SharpClaw.Tests.Providers;

[TestFixture]
public sealed class EdenAIApiClientTests
{
    [Test]
    public async Task ChatCompletionAsync_UsesEdenAiV3ChatCompletionsEndpoint()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new EdenAIApiClient();

        var result = await client.ChatCompletionAsync(
            httpClient,
            "test-key",
            "openai/gpt-4o-mini",
            systemPrompt: null,
            [new ChatCompletionMessage("user", "Hello")],
            completionParameters: new CompletionParameters
            {
                Temperature = 0.2f,
                ReasoningEffort = "none"
            });

        result.Content.Should().Be("ok");
        result.Usage.Should().Be(new TokenUsage(1, 2));
        handler.LastRequestUri?.ToString()
            .Should().Be("https://api.edenai.run/v3/chat/completions");
        handler.LastAuthorization?.Scheme.Should().Be("Bearer");
        handler.LastAuthorization?.Parameter.Should().Be("test-key");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString()
            .Should().Be("openai/gpt-4o-mini");
        doc.RootElement.GetProperty("temperature").GetDouble()
            .Should().BeApproximately(0.2, 0.000001);
        doc.RootElement.GetProperty("reasoning_effort").GetString()
            .Should().Be("none");
    }

    [Test]
    public async Task ListModelIdsAsync_UsesEdenAiV3ModelsEndpoint()
    {
        using var handler = new CaptureHandler("""
            {
              "object": "list",
              "data": [
                { "id": "openai/gpt-4o-mini", "object": "model" },
                { "id": "anthropic/claude-sonnet-4-5", "object": "model" }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new EdenAIApiClient();

        var ids = await client.ListModelIdsAsync(httpClient, "test-key");

        handler.LastRequestUri?.ToString()
            .Should().Be("https://api.edenai.run/v3/models");
        handler.LastAuthorization?.Scheme.Should().Be("Bearer");
        handler.LastAuthorization?.Parameter.Should().Be("test-key");
        ids.Should().Equal("anthropic/claude-sonnet-4-5", "openai/gpt-4o-mini");
    }

    [Test]
    public void ModuleRegistersEdenAiProvider()
    {
        var services = new ServiceCollection();
        new OpenAICompatibleProvidersModule().ConfigureServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        var plugin = serviceProvider.GetServices<IProviderPlugin>()
            .Single(p => p.ProviderKey == "eden-ai");

        plugin.DisplayName.Should().Be("Eden AI");
        plugin.OwnerModuleId.Should().Be("sharpclaw_providers_openai_compat");
        plugin.ParameterSpec.Should().BeSameAs(ProviderParameterSpecs.EdenAI);
        plugin.CreateClient(null).Should().BeOfType<EdenAIApiClient>();
        plugin.Capabilities.Resolve("openai/gpt-4o-mini")
            .Should().BeEquivalentTo([WellKnownCapabilityKeys.Chat, WellKnownCapabilityKeys.Vision]);
        plugin.Capabilities.Resolve("@edenai")
            .Should().BeEquivalentTo([WellKnownCapabilityKeys.Chat]);
    }

    [Test]
    public void ParameterSpecMatchesEdenAiSurface()
    {
        var spec = ProviderParameterSpecs.For("eden-ai");

        spec.ProviderName.Should().Be("Eden AI");
        spec.SupportsResponseFormat.Should().BeTrue();
        spec.SupportsReasoningEffort.Should().BeTrue();
        spec.ValidReasoningEffortValues.Should().Contain("none");
        spec.SupportsToolChoice.Should().BeTrue();
        spec.SupportsSeed.Should().BeTrue();
    }

    [Test]
    public void ProviderTypesAreDerivedFromRegisteredPlugins()
    {
        var services = new ServiceCollection();
        new OpenAICompatibleProvidersModule().ConfigureServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        var factory = new ProviderApiClientFactory(serviceProvider.GetServices<IProviderPlugin>());

        var types = new ProviderCatalogEngine().ListAvailableTypes(factory.Plugins);

        types.Should().ContainSingle(t =>
            t.ProviderKey == "eden-ai"
            && t.DisplayName == "Eden AI"
            && !t.RequiresEndpoint
            && t.RequiresApiKey
            && !t.SupportsDeviceCodeAuth);
        types.Should().Contain(t => t.ProviderKey == "google-gemini-openai");
    }

    private sealed class CaptureHandler(string? responseBody = null) : HttpMessageHandler
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
                "completion_tokens": 2,
                "total_tokens": 3
              }
            }
            """;

        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

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
