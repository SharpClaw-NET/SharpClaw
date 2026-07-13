using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.TestFixtures.ExternalModule;

public sealed class SyntheticExternalLifecycleModule : ISharpClawCoreModule
{
    public const string ModuleId = "synthetic_external_lifecycle";
    public const string ToolPrefixValue = "sel";
    public const string ProviderKey = "synthetic-external-provider";
    public const string ModelId = "synthetic-external-model";
    public const string HeaderTag = "synthetic_external_tag";
    public const string InlineTool = "synthetic_external_inline";
    public const string JobTool = "synthetic_external_job";
    public const string ResourceType = "SharpClaw.SyntheticExternal.Resource";
    public const string DefaultResourceKey = "synthetic_external_resource";
    public const string DelegateName = "UseSyntheticExternalResourceAsync";
    public const string TriggerKey = "synthetic_external_schedule";
    public const string ChatText = "external provider response";

    public string Id => ModuleId;
    public string DisplayName => "Synthetic External Lifecycle";
    public string ToolPrefix => ToolPrefixValue;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IProviderPlugin, SyntheticExternalProviderPlugin>();
        services.AddSingleton<ITaskTriggerSource, SyntheticExternalTriggerSource>();
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
    [
        new(
            JobTool,
            "External lifecycle job tool.",
            EmptySchema(),
            new ModuleToolPermission(
                false,
                (_, _, _, _) => Task.FromResult(
                    AgentActionResult.Approve(
                        "Synthetic external module approved.",
                        PermissionClearance.Independent))))
    ];

    public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() =>
    [
        new(
            InlineTool,
            "External lifecycle inline tool.",
            EmptySchema(),
            new ModuleToolPermission(
                false,
                (_, _, _, _) => Task.FromResult(
                    AgentActionResult.Approve(
                        "Synthetic external inline approved.",
                        PermissionClearance.Independent))))
    ];

    public IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() =>
    [
        new(HeaderTag, (_, _) => Task.FromResult("synthetic external header"))
    ];

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
    [
        new(
            ResourceType,
            "SyntheticExternalResource",
            DelegateName,
            (_, _) => Task.FromResult(new List<Guid> { Guid.Parse("11111111-1111-1111-1111-111111111111") }),
            (_, _) => Task.FromResult(new List<(Guid Id, string Name)>
            {
                (Guid.Parse("11111111-1111-1111-1111-111111111111"), "Synthetic resource")
            }),
            DefaultResourceKey)
    ];

    public Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var value = parameters.TryGetProperty("value", out var property)
            ? property.GetString()
            : "missing";
        return Task.FromResult($"external job {value}");
    }

    public Task<string> ExecuteInlineToolAsync(
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        Task.FromResult("external inline");

    private static JsonElement EmptySchema()
    {
        using var doc = JsonDocument.Parse("""{"type":"object","additionalProperties":false}""");
        return doc.RootElement.Clone();
    }

    private sealed class SyntheticExternalProviderPlugin : IProviderPlugin
    {
        public string ProviderKey => SyntheticExternalLifecycleModule.ProviderKey;
        public string DisplayName => "Synthetic External Provider";
        public string OwnerModuleId => ModuleId;
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public IModelCapabilityResolver Capabilities { get; } = new SyntheticExternalCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds { get; } = [new(ModelId, 0.01m, 0.02m)];
        public ICompletionParameterSpec ParameterSpec => ICompletionParameterSpec.Passthrough;
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public bool SupportsCostFeed => true;

        public IProviderApiClient CreateClient(ProviderClientOptions options) =>
            new SyntheticExternalProviderClient();

        public IProviderCostFeed? CreateCostFeed(ProviderClientOptions options) =>
            new SyntheticExternalCostFeed();
    }

    private sealed class SyntheticExternalCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => ["chat"];
    }

    private sealed class SyntheticExternalCostFeed : IProviderCostFeed
    {
        public Task<ProviderCostResult?> GetCostsAsync(
            DateTimeOffset startTime,
            DateTimeOffset? endTime,
            CancellationToken ct = default) =>
            Task.FromResult<ProviderCostResult?>(new ProviderCostResult(
                3.21m,
                "usd",
                [new ProviderCostDailyBucket(startTime, endTime ?? startTime.AddDays(1), 3.21m)]));
    }

    private sealed class SyntheticExternalTriggerSource : ITaskTriggerSource
    {
        public string? TriggerKey => SyntheticExternalLifecycleModule.TriggerKey;

        public Task StartAsync(
            IReadOnlyList<ITaskTriggerSourceContext> contexts,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class SyntheticExternalProviderClient : IProviderApiClient
    {
        public string ProviderKey => SyntheticExternalLifecycleModule.ProviderKey;

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
