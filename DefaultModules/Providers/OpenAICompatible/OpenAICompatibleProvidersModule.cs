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
            options => new OpenAiApiClient(options.ApiKey), openAiCaps,
            parameterSpec: ProviderParameterSpecs.OpenAI,
            costFeedFactory: options => new OpenAiApiClient(options.ApiKey),
            costFeedPermissionDeniedNote:
                "Cost API is available for this provider but the current API key "
                + "lacks the required permissions. OpenAI requires an admin key — "
                + "replace the configured key with an admin key to retrieve cost data.",
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "deepseek", "DeepSeek", false,
            options => new DeepSeekApiClient(options.ApiKey), deepSeekCaps,
            parameterSpec: ProviderParameterSpecs.DeepSeek,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "openrouter", "OpenRouter", false,
            options => new OpenRouterApiClient(options.ApiKey), genericCaps,
            parameterSpec: ProviderParameterSpecs.OpenRouter,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "eden-ai", "Eden AI", false,
            options => new EdenAIApiClient(options.ApiKey), edenCaps,
            parameterSpec: ProviderParameterSpecs.EdenAI,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "google-gemini-openai", "Google Gemini (OpenAI)", false,
            options => new GoogleGeminiOpenAiApiClient(options.ApiKey), googleCaps,
            parameterSpec: ProviderParameterSpecs.GoogleGeminiOpenAi,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "google-vertex-ai-openai", "Google Vertex AI (OpenAI)", false,
            options => new GoogleVertexAIOpenAiApiClient(options.ApiKey), googleCaps,
            parameterSpec: ProviderParameterSpecs.GoogleVertexAIOpenAi,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "zai", "Z.AI", false,
            options => new ZAIApiClient(options.ApiKey), genericCaps,
            parameterSpec: ProviderParameterSpecs.ZAI,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "vercel-ai-gateway", "Vercel AI Gateway", false,
            options => new VercelAIGatewayApiClient(options.ApiKey), genericCaps,
            parameterSpec: ProviderParameterSpecs.VercelAIGateway,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "xai", "xAI", false,
            options => new XAIApiClient(options.ApiKey), xaiCaps,
            parameterSpec: ProviderParameterSpecs.XAI,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "groq", "Groq", false,
            options => new GroqApiClient(options.ApiKey), genericCaps,
            parameterSpec: ProviderParameterSpecs.Groq,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "cerebras", "Cerebras", false,
            options => new CerebrasApiClient(options.ApiKey), genericCaps,
            parameterSpec: ProviderParameterSpecs.Cerebras,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "mistral", "Mistral", false,
            options => new MistralApiClient(options.ApiKey), mistralCaps,
            parameterSpec: ProviderParameterSpecs.Mistral,
            ownerModuleId: owner));

        // GitHub Copilot uses a singleton device-code helper and creates
        // provider clients with the saved OAuth token for chat requests.
        var copilotAuth = new GitHubCopilotApiClient();
        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "github-copilot", "GitHub Copilot", false,
            options => new GitHubCopilotApiClient(options.ApiKey), genericCaps,
            parameterSpec: ProviderParameterSpecs.GitHubCopilot,
            deviceCodeFlow: new DeviceCodeAuthClientFlow(copilotAuth),
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "minimax", "MiniMax", false,
            options => new MinimaxApiClient(options.ApiKey), minimaxCaps,
            parameterSpec: ProviderParameterSpecs.Minimax,
            ownerModuleId: owner));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "custom", "Custom (OpenAI-compatible)", true,
            options => new CustomOpenAiCompatibleApiClient(options.Endpoint!, options.ApiKey), genericCaps,
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
        CancellationToken ct = default)
        => inner.StartDeviceCodeFlowAsync(ct);

    public async Task<string?> PollAsync(
        SharpClaw.Contracts.DTOs.Providers.DeviceCodeSession session,
        CancellationToken ct = default)
        => await inner.PollForAccessTokenAsync(session, ct);
}
