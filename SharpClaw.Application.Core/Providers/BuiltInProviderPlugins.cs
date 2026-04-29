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
        var caps = new DelegateCapabilityResolver(ProviderService.InferCapabilitiesAndTags);

        IProviderPlugin Stateless(string key, string name, Func<IProviderApiClient> ctor, IDeviceCodeFlow? flow = null)
            => new SimpleProviderPlugin(key, name, requiresEndpoint: false, _ => ctor(), caps, deviceCodeFlow: flow);

        var copilot = new GitHubCopilotApiClient();

        return
        [
            Stateless(WellKnownProviderKeys.OpenAI,                 "OpenAI",                  () => new OpenAiApiClient()),
            Stateless(WellKnownProviderKeys.Anthropic,              "Anthropic",               () => new AnthropicApiClient()),
            Stateless(WellKnownProviderKeys.OpenRouter,             "OpenRouter",              () => new OpenRouterApiClient()),
            Stateless(WellKnownProviderKeys.GoogleVertexAI,         "Google Vertex AI",        () => new GoogleVertexAIApiClient()),
            Stateless(WellKnownProviderKeys.GoogleVertexAIOpenAi,   "Google Vertex AI (OpenAI)", () => new GoogleVertexAIOpenAiApiClient()),
            Stateless(WellKnownProviderKeys.GoogleGemini,           "Google Gemini",           () => new GoogleGeminiApiClient()),
            Stateless(WellKnownProviderKeys.GoogleGeminiOpenAi,     "Google Gemini (OpenAI)",  () => new GoogleGeminiOpenAiApiClient()),
            Stateless(WellKnownProviderKeys.ZAI,                    "Z.AI",                    () => new ZAIApiClient()),
            Stateless(WellKnownProviderKeys.VercelAIGateway,        "Vercel AI Gateway",       () => new VercelAIGatewayApiClient()),
            Stateless(WellKnownProviderKeys.XAI,                    "xAI",                     () => new XAIApiClient()),
            Stateless(WellKnownProviderKeys.Groq,                   "Groq",                    () => new GroqApiClient()),
            Stateless(WellKnownProviderKeys.Cerebras,               "Cerebras",                () => new CerebrasApiClient()),
            Stateless(WellKnownProviderKeys.Mistral,                "Mistral",                 () => new MistralApiClient()),
            Stateless(WellKnownProviderKeys.GitHubCopilot,          "GitHub Copilot",          () => copilot, flow: new DeviceCodeAuthClientFlow(copilot)),
            Stateless(WellKnownProviderKeys.Minimax,                "MiniMax",                 () => new MinimaxApiClient()),
            new SimpleProviderPlugin(
                WellKnownProviderKeys.LlamaSharp,
                "LlamaSharp (local)",
                requiresEndpoint: false,
                _ => new LocalInferenceApiClient(localInferenceManager),
                caps),
            new SimpleProviderPlugin(
                WellKnownProviderKeys.Ollama,
                "Ollama (local)",
                requiresEndpoint: false,
                endpoint => new OllamaApiClient(endpoint),
                caps),
            new SimpleProviderPlugin(
                WellKnownProviderKeys.Custom,
                "Custom (OpenAI-compatible)",
                requiresEndpoint: true,
                endpoint => new CustomOpenAiCompatibleApiClient(endpoint!),
                caps),
        ];
    }
}
