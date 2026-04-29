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
public sealed class OllamaProviderModule : ISharpClawModule
{
    public string Id => "sharpclaw_providers_ollama";
    public string DisplayName => "Ollama Provider";
    public string ToolPrefix => "po2";

    public void ConfigureServices(IServiceCollection services)
    {
        var caps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGeneric);
        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.Ollama, "Ollama (local)", false,
            endpoint => new OllamaApiClient(endpoint), caps,
            ownerModuleId: "sharpclaw_providers_ollama"));
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
        => throw new InvalidOperationException(
            $"Module '{Id}' does not register any tools.");
}
