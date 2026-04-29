using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.Google.Clients;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.Google;

/// <summary>
/// Default module: registers the native Google provider plugins (Gemini and
/// Vertex AI). Uses Google's <c>generateContent</c> wire format (not the
/// OpenAI-compatible shim — that lives in
/// <c>SharpClaw.Modules.Providers.OpenAICompatible</c>).
/// </summary>
public sealed class GoogleProvidersModule : ISharpClawModule
{
    public string Id => "sharpclaw_providers_google";
    public string DisplayName => "Google Native Providers";
    public string ToolPrefix => "pg";

    public void ConfigureServices(IServiceCollection services)
    {
        var caps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGoogle);

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.GoogleVertexAI, "Google Vertex AI", false,
            _ => new GoogleVertexAIApiClient(), caps,
            ownerModuleId: "sharpclaw_providers_google"));

        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.GoogleGemini, "Google Gemini", false,
            _ => new GoogleGeminiApiClient(), caps,
            ownerModuleId: "sharpclaw_providers_google"));
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
        => throw new InvalidOperationException(
            $"Module '{Id}' does not register any tools.");
}
