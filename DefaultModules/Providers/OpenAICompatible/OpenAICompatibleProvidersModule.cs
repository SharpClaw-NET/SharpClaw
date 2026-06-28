using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.OpenAICompatible.Clients;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible;

/// <summary>
/// Default module: registers the OpenAI-protocol family of provider plugins
/// (OpenAI, DeepSeek, OpenRouter, Eden AI, ZAI, Vercel AI Gateway, xAI, Groq, Cerebras,
/// Mistral, GitHub Copilot, Minimax, Custom, Google Gemini OpenAI shim,
/// Google Vertex AI OpenAI shim). All fifteen share <see cref="OpenAiCompatibleApiClient"/>
/// as the wire-format base; the heuristics are imported from
/// <see cref="ProviderCapabilityHeuristics"/>.
/// </summary>
public sealed class OpenAICompatibleProvidersModule : ISharpClawCoreModule
{
    public string Id => "sharpclaw_providers_openai_compat";
    public string DisplayName => "OpenAI-Compatible Providers";
    public string ToolPrefix => "po";

    public void ConfigureServices(IServiceCollection services)
    {
        var openAiCaps  = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForOpenAI);
        var googleCaps  = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGoogle);
        var deepSeekCaps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForDeepSeek);
        var edenCaps    = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForEdenAI);
        var mistralCaps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForMistral);
        var xaiCaps     = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForXai);
        var minimaxCaps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForMinimax);
        var genericCaps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGeneric);

        const string owner = "sharpclaw_providers_openai_compat";

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "openai", "OpenAI", false,
            _ => new OpenAiApiClient(), openAiCaps,
            parameterSpec: ProviderParameterSpecs.OpenAI,
            costFeed: new OpenAiApiClient(),
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "deepseek", "DeepSeek", false,
            _ => new DeepSeekApiClient(), deepSeekCaps,
            parameterSpec: ProviderParameterSpecs.DeepSeek,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "openrouter", "OpenRouter", false,
            _ => new OpenRouterApiClient(), genericCaps,
            parameterSpec: ProviderParameterSpecs.OpenRouter,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "eden-ai", "Eden AI", false,
            _ => new EdenAIApiClient(), edenCaps,
            parameterSpec: ProviderParameterSpecs.EdenAI,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "google-gemini-openai", "Google Gemini (OpenAI)", false,
            _ => new GoogleGeminiOpenAiApiClient(), googleCaps,
            parameterSpec: ProviderParameterSpecs.GoogleGeminiOpenAi,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "google-vertex-ai-openai", "Google Vertex AI (OpenAI)", false,
            _ => new GoogleVertexAIOpenAiApiClient(), googleCaps,
            parameterSpec: ProviderParameterSpecs.GoogleVertexAIOpenAi,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "zai", "Z.AI", false,
            _ => new ZAIApiClient(), genericCaps,
            parameterSpec: ProviderParameterSpecs.ZAI,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "vercel-ai-gateway", "Vercel AI Gateway", false,
            _ => new VercelAIGatewayApiClient(), genericCaps,
            parameterSpec: ProviderParameterSpecs.VercelAIGateway,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "xai", "xAI", false,
            _ => new XAIApiClient(), xaiCaps,
            parameterSpec: ProviderParameterSpecs.XAI,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "groq", "Groq", false,
            _ => new GroqApiClient(), genericCaps,
            parameterSpec: ProviderParameterSpecs.Groq,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "cerebras", "Cerebras", false,
            _ => new CerebrasApiClient(), genericCaps,
            parameterSpec: ProviderParameterSpecs.Cerebras,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "mistral", "Mistral", false,
            _ => new MistralApiClient(), mistralCaps,
            parameterSpec: ProviderParameterSpecs.Mistral,
            ownerModuleId: owner));

        // GitHub Copilot reuses one client instance for both the API
        // client factory and the device-code flow adapter so the cached
        // OAuth token is shared across both surfaces.
        var copilot = new GitHubCopilotApiClient();
        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "github-copilot", "GitHub Copilot", false,
            _ => copilot, genericCaps,
            parameterSpec: ProviderParameterSpecs.GitHubCopilot,
            deviceCodeFlow: new DeviceCodeAuthClientFlow(copilot),
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "minimax", "MiniMax", false,
            _ => new MinimaxApiClient(), minimaxCaps,
            parameterSpec: ProviderParameterSpecs.Minimax,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "custom", "Custom (OpenAI-compatible)", true,
            endpoint => new CustomOpenAiCompatibleApiClient(endpoint!), genericCaps,
            parameterSpec: ProviderParameterSpecs.Custom,
            isSeedable: false,
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
