using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.Anthropic.Clients;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.Anthropic;

/// <summary>
/// Default module: registers the native Anthropic provider plugin.
/// Uses Anthropic's <c>/v1/messages</c> wire format (not OpenAI-compatible).
/// </summary>
public sealed class AnthropicProviderModule : ISharpClawModule
{
    public string Id => "sharpclaw_providers_anthropic";
    public string DisplayName => "Anthropic Provider";
    public string ToolPrefix => "pa";

    public void ConfigureServices(IServiceCollection services)
    {
        var caps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForAnthropic);
        services.AddSingleton<IProviderPlugin>(new SimpleProviderPlugin(
            WellKnownProviderKeys.Anthropic, "Anthropic", false,
            _ => new AnthropicApiClient(), caps,
            ownerModuleId: "sharpclaw_providers_anthropic"));
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
        => throw new InvalidOperationException(
            $"Module '{Id}' does not register any tools.");
}
