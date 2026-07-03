using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.Ollama.Clients;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.Ollama;

/// <summary>
/// Default module: registers the Ollama provider plugin (a thin
/// <see cref="OpenAiCompatibleApiClient"/> subclass that targets a
/// user-managed Ollama server and overrides model listing to use
/// Ollama's <c>/api/tags</c> endpoint).
/// </summary>
public sealed class OllamaProviderModule : ISharpClawCoreModule
{
    public string Id => "sharpclaw_providers_ollama";
    public string DisplayName => "Ollama Provider";
    public string ToolPrefix => "po2";

    public void ConfigureServices(IServiceCollection services)
    {
        var caps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGeneric);
        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            "ollama", "Ollama (local)", false,
            options => new OllamaApiClient(options.Endpoint, options.ApiKey), caps,
            parameterSpec: ProviderParameterSpecs.Ollama,
            supportsAutomaticEndpointDiscovery: true,
            requiresApiKey: false,
            ownerModuleId: "sharpclaw_providers_ollama"));
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
        => throw new InvalidOperationException(
            $"Module '{Id}' does not register any tools.");
}
