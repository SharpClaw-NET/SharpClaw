using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.OpenAICompatible.Clients;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible;

/// <summary>
/// Default module: registers the OpenAI-protocol family of provider plugins
/// (OpenAI, OpenRouter, ZAI, Vercel AI Gateway, xAI, Groq, Cerebras, Mistral,
/// GitHub Copilot, Minimax, Custom, Google Gemini OpenAI shim, Google Vertex
/// AI OpenAI shim). All thirteen share <see cref="OpenAiCompatibleApiClient"/>
/// as the wire-format base; the heuristics are imported from
/// <see cref="ProviderCapabilityHeuristics"/>.
/// </summary>
public sealed class OpenAICompatibleProvidersModule : ISharpClawModule
{
    public string Id => "sharpclaw_providers_openai_compat";
    public string DisplayName => "OpenAI-Compatible Providers";
    public string ToolPrefix => "po";

    public void ConfigureServices(IServiceCollection services)
    {
        var openAiCaps  = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForOpenAI);
        var googleCaps  = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGoogle);
        var mistralCaps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForMistral);
        var xaiCaps     = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForXai);
        var minimaxCaps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForMinimax);
        var genericCaps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGeneric);

        const string owner = "sharpclaw_providers_openai_compat";

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.OpenAI, "OpenAI", false,
            _ => new OpenAiApiClient(), openAiCaps,
            costFeed: new OpenAiApiClient(),
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.OpenRouter, "OpenRouter", false,
            _ => new OpenRouterApiClient(), genericCaps,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.GoogleGeminiOpenAi, "Google Gemini (OpenAI)", false,
            _ => new GoogleGeminiOpenAiApiClient(), googleCaps,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.GoogleVertexAIOpenAi, "Google Vertex AI (OpenAI)", false,
            _ => new GoogleVertexAIOpenAiApiClient(), googleCaps,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.ZAI, "Z.AI", false,
            _ => new ZAIApiClient(), genericCaps,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.VercelAIGateway, "Vercel AI Gateway", false,
            _ => new VercelAIGatewayApiClient(), genericCaps,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.XAI, "xAI", false,
            _ => new XAIApiClient(), xaiCaps,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.Groq, "Groq", false,
            _ => new GroqApiClient(), genericCaps,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.Cerebras, "Cerebras", false,
            _ => new CerebrasApiClient(), genericCaps,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.Mistral, "Mistral", false,
            _ => new MistralApiClient(), mistralCaps,
            ownerModuleId: owner));

        // GitHub Copilot reuses one client instance for both the API
        // client factory and the device-code flow adapter so the cached
        // OAuth token is shared across both surfaces.
        var copilot = new GitHubCopilotApiClient();
        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.GitHubCopilot, "GitHub Copilot", false,
            _ => copilot, genericCaps,
            deviceCodeFlow: new DeviceCodeAuthClientFlow(copilot),
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.Minimax, "MiniMax", false,
            _ => new MinimaxApiClient(), minimaxCaps,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.Custom, "Custom (OpenAI-compatible)", true,
            endpoint => new CustomOpenAiCompatibleApiClient(endpoint!), genericCaps,
            ownerModuleId: owner));
    }

    // No tools, resources, endpoints, or CLI commands — this module only
    // contributes provider plugins through DI.
    public IReadOnlyList<ModuleContractExport> ExportedContracts => [];
    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() => [];
    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];
    public IReadOnlyList<ModuleCliCommand> GetCliCommands() => [];

    public void MapEndpoints(object app) { }

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
        => throw new InvalidOperationException(
            $"Module '{Id}' does not register any tools.");
}

/// <summary>
/// Adapter exposing an <see cref="IDeviceCodeAuthClient"/> implementation
/// (used internally by <see cref="GitHubCopilotApiClient"/>) through the
/// <see cref="IDeviceCodeFlow"/> plugin contract surfaced by
/// <see cref="IProviderPlugin"/>.
/// </summary>
internal sealed class DeviceCodeAuthClientFlow(IDeviceCodeAuthClient inner) : IDeviceCodeFlow
{
    public Task<SharpClaw.Contracts.DTOs.Providers.DeviceCodeSession> StartAsync(
        HttpClient httpClient, CancellationToken ct = default)
        => inner.StartDeviceCodeFlowAsync(httpClient, ct);

    public async Task<string?> PollAsync(
        HttpClient httpClient,
        SharpClaw.Contracts.DTOs.Providers.DeviceCodeSession session,
        CancellationToken ct = default)
        => await inner.PollForAccessTokenAsync(httpClient, session, ct);
}
