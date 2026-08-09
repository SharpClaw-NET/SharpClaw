using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;

public sealed class RuntimeProviderClientFactory : IRuntimeProviderClientFactory
{
    public IProviderApiClient Create(
        IConfiguration configuration,
        IReadOnlyList<IProviderPlugin> plugins) =>
        new RuntimeProviderClient(configuration, plugins);
}

/// <summary>Resolves the configured provider from host-owned provider plugins.</summary>
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
        return ProviderCredentialBinding.CreateClient(
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
        Dictionary<string, JsonElement>? providerParameters = null,
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
        Dictionary<string, JsonElement>? providerParameters = null,
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
        Dictionary<string, JsonElement>? providerParameters = null,
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
