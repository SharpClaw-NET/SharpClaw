using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Application.Core.Services;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Application.Core.Providers;

/// <summary>
/// Adapter that exposes the existing
/// <see cref="IDeviceCodeAuthClient"/> implementation on
/// <c>GitHubCopilotApiClient</c> through the new
/// <see cref="IDeviceCodeFlow"/> plugin contract. Removed in Phase 6
/// when the OpenAI-compatible module owns the flow directly.
/// </summary>
internal sealed class DeviceCodeAuthClientFlow(IDeviceCodeAuthClient inner) : IDeviceCodeFlow
{
    public Task<DeviceCodeSession> StartAsync(HttpClient httpClient, CancellationToken ct = default)
        => inner.StartDeviceCodeFlowAsync(httpClient, ct);

    public async Task<string?> PollAsync(HttpClient httpClient, DeviceCodeSession session, CancellationToken ct = default)
        => await inner.PollForAccessTokenAsync(httpClient, session, ct);
}

/// <summary>
/// Builds the seventeen built-in provider plugins that ship inside
/// Core for Phase 3. Each entry mirrors a row that previously lived in
/// <c>ProviderApiClientFactory</c>'s implicit dispatch table. Phases 6
/// through 9 carve these entries out into per-module plugin classes
/// and delete this file.
/// </summary>
public static class BuiltInProviderPlugins
{
    public static IEnumerable<IProviderPlugin> Build(LocalInferenceProcessManager localInferenceManager)
    {
        var openAiCaps    = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForOpenAI);
        var anthropicCaps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForAnthropic);
        var googleCaps    = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGoogle);
        var mistralCaps   = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForMistral);
        var xaiCaps       = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForXai);
        var minimaxCaps   = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForMinimax);
        var genericCaps   = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGeneric);

        IProviderPlugin Stateless(string key, string name, Func<IProviderApiClient> ctor,
            IModelCapabilityResolver caps, IDeviceCodeFlow? flow = null)
            => new SimpleProviderPlugin(key, name, requiresEndpoint: false, _ => ctor(), caps, deviceCodeFlow: flow);

        var copilot = new GitHubCopilotApiClient();

        return
        [
            Stateless(WellKnownProviderKeys.OpenAI,                 "OpenAI",                    () => new OpenAiApiClient(),                openAiCaps),
            Stateless(WellKnownProviderKeys.Anthropic,              "Anthropic",                 () => new AnthropicApiClient(),             anthropicCaps),
            Stateless(WellKnownProviderKeys.OpenRouter,             "OpenRouter",                () => new OpenRouterApiClient(),            genericCaps),
            Stateless(WellKnownProviderKeys.GoogleVertexAI,         "Google Vertex AI",          () => new GoogleVertexAIApiClient(),        googleCaps),
            Stateless(WellKnownProviderKeys.GoogleVertexAIOpenAi,   "Google Vertex AI (OpenAI)", () => new GoogleVertexAIOpenAiApiClient(),  googleCaps),
            Stateless(WellKnownProviderKeys.GoogleGemini,           "Google Gemini",             () => new GoogleGeminiApiClient(),          googleCaps),
            Stateless(WellKnownProviderKeys.GoogleGeminiOpenAi,     "Google Gemini (OpenAI)",    () => new GoogleGeminiOpenAiApiClient(),    googleCaps),
            Stateless(WellKnownProviderKeys.ZAI,                    "Z.AI",                      () => new ZAIApiClient(),                   genericCaps),
            Stateless(WellKnownProviderKeys.VercelAIGateway,        "Vercel AI Gateway",         () => new VercelAIGatewayApiClient(),       genericCaps),
            Stateless(WellKnownProviderKeys.XAI,                    "xAI",                       () => new XAIApiClient(),                   xaiCaps),
            Stateless(WellKnownProviderKeys.Groq,                   "Groq",                      () => new GroqApiClient(),                  genericCaps),
            Stateless(WellKnownProviderKeys.Cerebras,               "Cerebras",                  () => new CerebrasApiClient(),              genericCaps),
            Stateless(WellKnownProviderKeys.Mistral,                "Mistral",                   () => new MistralApiClient(),               mistralCaps),
            Stateless(WellKnownProviderKeys.GitHubCopilot,          "GitHub Copilot",            () => copilot,                              genericCaps, flow: new DeviceCodeAuthClientFlow(copilot)),
            Stateless(WellKnownProviderKeys.Minimax,                "MiniMax",                   () => new MinimaxApiClient(),               minimaxCaps),
            new SimpleProviderPlugin(
                WellKnownProviderKeys.LlamaSharp,
                "LlamaSharp (local)",
                requiresEndpoint: false,
                _ => new LocalInferenceApiClient(localInferenceManager),
                genericCaps),
            new SimpleProviderPlugin(
                WellKnownProviderKeys.Ollama,
                "Ollama (local)",
                requiresEndpoint: false,
                endpoint => new OllamaApiClient(endpoint),
                genericCaps),
            new SimpleProviderPlugin(
                WellKnownProviderKeys.Custom,
                "Custom (OpenAI-compatible)",
                requiresEndpoint: true,
                endpoint => new CustomOpenAiCompatibleApiClient(endpoint!),
                genericCaps),
        ];
    }
}
