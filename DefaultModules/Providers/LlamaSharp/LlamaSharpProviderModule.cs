using System.Text.Json;
using LLama.Native;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.LlamaSharp.Cli;
using SharpClaw.Modules.Providers.LlamaSharp.Clients;
using SharpClaw.Modules.Providers.LlamaSharp.Handlers;
using SharpClaw.Modules.Providers.LlamaSharp.LocalInference;
using SharpClaw.Modules.Providers.LlamaSharp.Services;
using SharpClaw.Providers.Common;
using SharpClaw.Providers.LocalCommon;

namespace SharpClaw.Modules.Providers.LlamaSharp;

/// <summary>
/// Default module: registers the LlamaSharp (in-process llama.cpp)
/// provider plugin, owns the <c>LocalModelFile</c> entity in
/// <see cref="LlamaSharpDbContext"/>, and exposes <c>/models/local</c>
/// REST + <c>localmodel</c> CLI surfaces.
/// </summary>
public sealed class LlamaSharpProviderModule : ISharpClawModule
{
    private static int _nativeBackendConfigured;

    public string Id => "sharpclaw_providers_llamasharp";
    public string DisplayName => "LlamaSharp Provider";
    public string ToolPrefix => "po3";

    public void ConfigureServices(IServiceCollection services)
    {
        // L-015: configure the LLamaSharp native backend exactly once.
        // NativeLibraryConfig is sticky — the first call to a LLama API
        // freezes the backend selection, so this must run before any
        // LocalInferenceProcessManager allocation. Idempotent across
        // hot-reload module reloads.
        if (Interlocked.Exchange(ref _nativeBackendConfigured, 1) == 0)
        {
            NativeLibraryConfig.All
                .WithCuda(true)
                .WithVulkan(true)
                .WithAutoFallback(true)
                .WithLogCallback((level, message) =>
                {
                    if (level >= LLamaLogLevel.Warning)
                        System.Diagnostics.Debug.WriteLine(
                            $"[llama.cpp] {message?.TrimEnd()}", "SharpClaw.CLI");
                });
        }

        // Module-owned DbContext.
        services.AddScoped(sp => sp.GetRequiredService<IModuleDbContextFactory>()
            .CreateDbContext<LlamaSharpDbContext>());

        // Process manager (singleton, configured from Local:* keys).
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var processManager = new LocalInferenceProcessManager();
            if (int.TryParse(cfg["Local:GpuLayerCount"], out var gpuLayers))
                processManager.DefaultGpuLayerCount = gpuLayers;
            if (uint.TryParse(cfg["Local:ContextSize"], out var ctxSize))
                processManager.DefaultContextSize = ctxSize;
            if (int.TryParse(cfg["Local:IdleCooldownMinutes"], out var cooldownMin))
                processManager.IdleCooldown = TimeSpan.FromMinutes(cooldownMin);
            if (bool.TryParse(cfg["Local:KeepLoaded"], out var keepLoaded))
                processManager.KeepLoaded = keepLoaded;
            if (int.TryParse(cfg["Local:MaxCachedSessions"], out var maxCached) && maxCached > 0)
                processManager.MaxCachedSessions = maxCached;
            return processManager;
        });

        // Download / URL resolution helpers (host-agnostic — live in LocalCommon).
        services.AddSingleton<HuggingFaceUrlResolver>();
        services.AddSingleton<ModelDownloadManager>();

        // Module services.
        services.AddScoped<LocalModelService>();
        services.AddScoped<ILocalModelLookup, LocalModelLookup>();
        services.AddScoped<IChatLocalModelGate, ChatLocalModelGate>();

        // Provider plugin — in-process LLamaSharp client.
        services.AddSingleton<IProviderPlugin>(sp =>
        {
            var pm = sp.GetRequiredService<LocalInferenceProcessManager>();
            var caps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGeneric);
            return new SimpleProviderPlugin(
                WellKnownProviderKeys.LlamaSharp,
                "LlamaSharp (local)",
                requiresEndpoint: false,
                _ => new LocalInferenceApiClient(pm),
                caps,
                agentIdentifierSuffix: (providerName, sourceUrl) =>
                    string.IsNullOrEmpty(sourceUrl)
                        ? providerName.Replace(" ", "-").ToLowerInvariant()
                        : ModelDownloadManager.ResolveSourceFolder(sourceUrl).ToLowerInvariant(),
                ownerModuleId: "sharpclaw_providers_llamasharp");
        });
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "localmodel",
            Aliases: ["lm"],
            Scope: ModuleCliScope.TopLevel,
            Description: "Local GGUF model management (LlamaSharp)",
            UsageLines:
            [
                "localmodel download <url> [--name <alias>] [--quant <Q4_K_M>] [--gpu-layers <n>]",
                "localmodel download list <url>           List available GGUF files at a URL",
                "localmodel list                          List downloaded local models",
                "localmodel load <id> [--gpu-layers <n>] [--ctx <size>] [--mmproj <path>]",
                "localmodel unload <id>                   Unpin a loaded model",
                "localmodel mmproj <id> <path|none>       Set or clear CLIP/mmproj path",
                "localmodel delete <id>                   Remove a downloaded local model",
            ],
            Handler: LocalModelCliCommand.HandleAsync),
    ];

    public void MapEndpoints(object app)
    {
        var endpoints = (IEndpointRouteBuilder)app;
        endpoints.MapLocalModelEndpoints();
    }

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
        => throw new InvalidOperationException(
            $"Module '{Id}' does not register any tools.");

    public async Task ShutdownAsync()
    {
        // LocalInferenceProcessManager owns native handles — host DI
        // disposal handles the actual unload.
        await Task.CompletedTask;
    }
}
