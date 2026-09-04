using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;

public sealed class SyntheticExternalLifecycleRegistration : ISharpClawModule
{
    public const string SourceId = "synthetic_external_lifecycle";
    public const string ToolPrefixValue = "sel";
    public const string ProviderKey = "synthetic-external-provider";
    public const string ModelId = "synthetic-external-model";
    public const string InlineTool = "synthetic_external_inline";
    public const string JobTool = "synthetic_external_job";
    public const string ChatText = "external provider response";

    public ModuleIdentity Identity { get; } = new(SourceId, "Synthetic external lifecycle", ToolPrefixValue);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SyntheticExternalToolHandler>();
        services.AddSingleton<IProviderPlugin, SyntheticExternalProviderPlugin>();
        services.AddTool<SyntheticExternalToolHandler>(
            new ToolDescriptor(JobTool, "External lifecycle tool.", ToolSchemas.EmptyObject));
        services.AddTool<SyntheticExternalToolHandler>(
            new ToolDescriptor(InlineTool, "External lifecycle inline tool.", ToolSchemas.EmptyObject));
    }

    private sealed class SyntheticExternalToolHandler : IToolHandler
    {
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var value = invocation.Arguments.ValueKind == JsonValueKind.Object &&
                invocation.Arguments.TryGetProperty("value", out var property)
                ? property.GetString() ?? "missing"
                : "missing";
            return ValueTask.FromResult(ToolResult.Text($"external {value}"));
        }
    }

    private sealed class SyntheticExternalProviderPlugin : IProviderPlugin
    {
        public string ProviderKey => SyntheticExternalLifecycleRegistration.ProviderKey;
        public string DisplayName => "Synthetic External Provider";
        public string OwnerId => SourceId;
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public IModelCapabilityResolver Capabilities { get; } = new SyntheticExternalCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds { get; } = [];
        public IProviderApiClient CreateClient(ProviderClientOptions options) => new SyntheticExternalProviderClient();
        public IProviderCostFeed? CreateCostFeed(ProviderClientOptions options) => null;
    }

    private sealed class SyntheticExternalCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => ["chat"];
    }

    private sealed class SyntheticExternalProviderClient : IProviderApiClient
    {
        public string ProviderKey => SyntheticExternalLifecycleRegistration.ProviderKey;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([ModelId]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ChatCompletionResult
            {
                Content = ChatText,
                Usage = new TokenUsage(2, 3),
                FinishReason = FinishReason.Stop
            });
    }
}
