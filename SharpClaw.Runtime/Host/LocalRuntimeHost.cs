using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.INF.Configuration;
using SharpClaw.Runtime.INF;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;

namespace SharpClaw.Runtime.Host;

/// <summary>Builds and runs the authoritative local Runtime composition.</summary>
public static class LocalRuntimeHost
{
    public static async Task RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var instancePaths = RuntimeInstancePathResolver.CreateBackend();
        instancePaths.EnsureDirectories();
        instancePaths.CleanupStaleDiscoveryEntries(TimeSpan.FromMinutes(2));
        using var instanceLock = new SharpClawInstanceLock(instancePaths);

        var earlyConfiguration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddLocalEnvironment(isDevelopment: false, instancePaths)
            .Build();

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(earlyConfiguration);
        builder.WebHost.UseUrls(
            earlyConfiguration["ASPNETCORE_URLS"]
            ?? "http://127.0.0.1:48923");

        var runtimeBaseUrl = earlyConfiguration["ASPNETCORE_URLS"]
            ?? "http://127.0.0.1:48923";

        builder.Services.AddSingleton(instancePaths);
        var encryptionKey = EncryptionKeyResolver.ResolveKey(instancePaths)
            ?? throw new InvalidOperationException(
                "The Runtime application encryption key could not be resolved.");
        builder.Services.AddSingleton(new EncryptionOptions
        {
            Key = encryptionKey,
            EncryptProviderKeys = earlyConfiguration.GetValue(
                "Encryption:EncryptProviderKeys",
                defaultValue: true),
        });
        builder.Services.AddInfrastructure(
            DatabaseProviderOptions.FromConfiguration(
                earlyConfiguration,
                Path.Combine(instancePaths.DataDirectory, "database")));
        builder.Services.AddSingleton<ApiKeyProvider>();
        builder.Services.AddSingleton<RuntimeProviderClient>();
        builder.Services.AddSingleton<IProviderApiClient>(
            services => services.GetRequiredService<RuntimeProviderClient>());
        builder.Services.AddSingleton<IConversationResolver>(
            _ => new SingleConversationResolver(Guid.NewGuid()));
        builder.Services.AddSingleton<IChatProfileResolver>(services =>
            new FixedChatProfileResolver(CreateProfile(services.GetRequiredService<IConfiguration>())));
        builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        builder.Services.AddSingleton<DirectChatKernel>(services =>
            DirectChatKernelFactory.Create(
                new ProviderKernelTransport(services.GetRequiredService<RuntimeProviderClient>()),
                services.GetRequiredService<IConversationResolver>(),
                services.GetRequiredService<IChatProfileResolver>(),
                services.GetRequiredService<IConversationStore>()));

        var app = builder.Build();
        var apiKeyProvider = app.Services.GetRequiredService<ApiKeyProvider>();
        instancePaths.PublishDiscoveryEntry(runtimeBaseUrl);
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            apiKeyProvider.Cleanup();
            instancePaths.DeleteDiscoveryEntry();
        });

        app.UseMiddleware<ApiKeyMiddleware>();
        KernelHostEndpoints.Map(app);
        app.MapHandlers();

        await app.RunAsync();
    }

    private static ChatProfile CreateProfile(IConfiguration configuration)
    {
        var providerKey = configuration["Provider:Key"]
            ?? configuration["Providers:Default"]
            ?? "unconfigured";
        var modelName = configuration["Provider:Model"];
        return new ChatProfile(
            providerKey,
            Guid.Empty,
            modelName,
            configuration["Provider:SystemPrompt"]);
    }
}

/// <summary>Resolves one canonical provider client from host-bound plugins.</summary>
public sealed class RuntimeProviderClient(
    IConfiguration configuration,
    IEnumerable<IProviderPlugin> plugins) : IProviderApiClient
{
    private readonly Lazy<IProviderApiClient> _client = new(() =>
    {
        var providerKey = configuration["Provider:Key"]
            ?? configuration["Providers:Default"]
            ?? throw new InvalidOperationException(
                "Provider:Key must be configured before a provider call.");
        var plugin = plugins.FirstOrDefault(value =>
            string.Equals(value.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No enabled provider module registered provider '{providerKey}'.");
        var endpoint = configuration[$"Providers:{providerKey}:Endpoint"]
            ?? configuration["Provider:Endpoint"];
        var credential = configuration[$"Providers:{providerKey}:ApiKey"]
            ?? configuration["Provider:ApiKey"]
            ?? string.Empty;
        return SharpClaw.Providers.Common.ProviderCredentialBinding.CreateClient(
            plugin,
            new ProviderClientOptions(endpoint),
            credential);
    });

    public string ProviderKey => configuration["Provider:Key"]
        ?? configuration["Providers:Default"]
        ?? "unconfigured";

    public bool SupportsNativeToolCalling => _client.Value.SupportsNativeToolCalling;

    public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
        _client.Value.ListModelIdsAsync(ct);

    public Task<ChatCompletionResult> ChatCompletionAsync(
        string model,
        string? systemPrompt,
        IReadOnlyList<ChatCompletionMessage> messages,
        int? maxCompletionTokens = null,
        Dictionary<string, System.Text.Json.JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default) =>
        _client.Value.ChatCompletionAsync(
            model,
            systemPrompt,
            messages,
            maxCompletionTokens,
            providerParameters,
            completionParameters,
            ct);

    public Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, System.Text.Json.JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default) =>
        _client.Value.ChatCompletionWithToolsAsync(
            model,
            systemPrompt,
            messages,
            tools,
            maxCompletionTokens,
            providerParameters,
            completionParameters,
            ct);

    public IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, System.Text.Json.JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default) =>
        _client.Value.StreamChatCompletionWithToolsAsync(
            model,
            systemPrompt,
            messages,
            tools,
            maxCompletionTokens,
            providerParameters,
            completionParameters,
            ct);
}
