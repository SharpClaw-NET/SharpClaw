using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Modules.Foreign;

namespace SharpClaw.Runtime.BLL.Modules.Foreign;

internal sealed class ForeignModuleProxy(
    ModuleManifest manifest,
    ForeignModuleProtocolClient client,
    Func<Task> shutdown)
    : ISharpClawRuntimeModule, IForeignModuleProtocolContractExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private IReadOnlyList<ForeignModuleToolDescriptor> _tools = [];
    private IReadOnlyList<ForeignModuleInlineToolDescriptor> _inlineTools = [];
    private IReadOnlyList<ForeignModuleProtocolContractExportDescriptor> _protocolContracts = [];
    private IReadOnlyList<ForeignModuleProtocolContractRequirementDescriptor> _requiredProtocolContracts = [];
    private IReadOnlyList<ForeignModuleHeaderTagDescriptor> _headerTags = [];
    private IReadOnlyList<ForeignModuleResourceTypeDescriptor> _resourceTypes = [];
    private IReadOnlyList<ForeignModuleGlobalFlagDescriptor> _globalFlags = [];
    private IReadOnlyList<ModuleUiContribution> _uiContributions = [];
    private IReadOnlyList<ModuleFrontendContribution> _frontendContributions = [];
    private IReadOnlyList<ModuleStorageContractDescriptor> _storageContracts = [];
    private IReadOnlyList<ForeignModuleCliCommandDescriptor> _cliCommands = [];
    private IReadOnlyList<ForeignModuleProviderPluginDescriptor> _providerPlugins = [];

    public string Id => manifest.Id;
    public string DisplayName => manifest.DisplayName;
    public string ToolPrefix => manifest.ToolPrefix;

    public void ConfigureServices(IServiceCollection services)
    {
        foreach (var providerPlugin in _providerPlugins)
        {
            services.AddSingleton<IProviderPlugin>(
                new ForeignModuleProviderPlugin(manifest, client, providerPlugin));
        }
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [.. _tools.Select(tool => tool.ToModuleToolDefinition())];

    public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() =>
        [.. _inlineTools.Select(tool => tool.ToModuleInlineToolDefinition())];

    public IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() =>
        [.. _headerTags.Select(tag => tag.ToModuleHeaderTag(manifest, client))];

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
        [.. _resourceTypes.Select(resource => resource.ToModuleResourceTypeDescriptor(manifest, client))];

    public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
        [.. _globalFlags.Select(flag => flag.ToModuleGlobalFlagDescriptor())];

    public IReadOnlyList<ModuleUiContribution> GetUiContributions() => _uiContributions;

    public IReadOnlyList<ModuleFrontendContribution> GetFrontendContributions() => _frontendContributions;

    public IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts() => _storageContracts;

    public IReadOnlyList<ModuleCliCommand>? GetCliCommands() =>
        [.. _cliCommands.Select(command => command.ToModuleCliCommand(manifest, client))];

    public IReadOnlyList<ForeignModuleProtocolContractExport> ExportedProtocolContracts =>
        [.. _protocolContracts.Select(contract => contract.ToProtocolContractExport())];

    public IReadOnlyList<ForeignModuleProtocolContractRequirement> RequiredProtocolContracts =>
        [.. _requiredProtocolContracts.Select(contract => contract.ToProtocolContractRequirement())];

    public void ApplyDiscovery(ForeignModuleDiscoveryResponse discovery)
    {
        _tools = discovery.Tools ?? [];
        _inlineTools = discovery.InlineTools ?? [];
        _protocolContracts = discovery.ProtocolContracts ?? [];
        _requiredProtocolContracts = discovery.RequiredProtocolContracts ?? [];
        _headerTags = discovery.HeaderTags ?? [];
        _resourceTypes = discovery.ResourceTypes ?? [];
        _globalFlags = discovery.GlobalFlags ?? [];
        _uiContributions = discovery.UiContributions ?? [];
        _frontendContributions = discovery.FrontendContributions ?? [];
        _storageContracts = discovery.StorageContracts ?? [];
        _cliCommands = discovery.CliCommands ?? [];
        _providerPlugins = discovery.ProviderPlugins ?? [];
    }

    public IForeignModuleProtocolContractInvoker GetProtocolContractInvoker(string contractName)
    {
        var export = _protocolContracts.FirstOrDefault(contract =>
            string.Equals(contract.ContractName, contractName, StringComparison.Ordinal));
        if (export is null)
            throw new InvalidOperationException(
                $"Foreign module '{Id}' does not export protocol contract '{contractName}'.");

        return new ForeignModuleProtocolContractInvoker(
            manifest,
            client,
            export.ToProtocolContractExport());
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) =>
        client.InitializeAsync(manifest, ct);

    public Task ShutdownAsync() => shutdown();

    public async Task<ModuleHealthStatus> HealthCheckAsync(CancellationToken ct) =>
        (await client.HealthAsync(ct)).ToModuleHealthStatus();

    public Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        ExecuteToolCoreAsync(toolName, parameters, job, ct);

    public ModuleJobCompletionBehavior GetJobCompletionBehavior(
        string toolName,
        JsonElement parameters,
        AgentJobContext job)
    {
        var tool = _tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        if (tool?.SupportsDynamicCompletionBehavior == true)
        {
            return client.GetToolCompletionBehaviorAsync(
                    manifest,
                    toolName,
                    parameters,
                    job,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .CompletionBehavior;
        }

        return tool?.CompletionBehavior
            ?? ModuleJobCompletionBehavior.CompleteWhenExecutionReturns;
    }

    public Task<string> ExecuteInlineToolAsync(
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        ExecuteInlineToolCoreAsync(toolName, parameters, context, ct);

    public IAsyncEnumerable<string>? ExecuteToolStreamingAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var tool = _tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        return tool?.SupportsStreaming == true
            ? client.ExecuteToolStreamingAsync(manifest, toolName, parameters, job, ct)
            : null;
    }

    private async Task<string> ExecuteToolCoreAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        CancellationToken ct)
    {
        var response = await client.ExecuteToolAsync(manifest, toolName, parameters, job, ct);
        return response.Result ?? string.Empty;
    }

    private async Task<string> ExecuteInlineToolCoreAsync(
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        CancellationToken ct)
    {
        var response = await client.ExecuteInlineToolAsync(manifest, toolName, parameters, context, ct);
        return response.Result ?? string.Empty;
    }

    private sealed class ForeignModuleProviderPlugin(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleProviderPluginDescriptor descriptor) : IProviderPlugin
    {
        public string ProviderKey => descriptor.ProviderKey;
        public string DisplayName => descriptor.DisplayName;
        public string OwnerModuleId => descriptor.OwnerModuleId ?? manifest.Id;
        public bool RequiresEndpoint => descriptor.RequiresEndpoint;
        public bool SupportsAutomaticEndpointDiscovery => descriptor.SupportsAutomaticEndpointDiscovery;
        public bool IsSeedable => descriptor.IsSeedable;
        public bool RequiresApiKey => descriptor.RequiresApiKey;
        public IReadOnlyList<ProviderCostSeed> CostSeeds => descriptor.CostSeeds ?? [];
        public ICompletionParameterSpec ParameterSpec { get; } =
            new ForeignModuleCompletionParameterSpec(
                descriptor.ParameterSpec
                ?? new ForeignModuleCompletionParameterSpecDescriptor(descriptor.DisplayName));

        public IModelCapabilityResolver Capabilities { get; } =
            new ForeignModuleModelCapabilityResolver(manifest, client, descriptor.ProviderKey);

        public IDeviceCodeFlow? DeviceCodeFlow =>
            descriptor.SupportsDeviceCodeFlow
                ? new ForeignModuleDeviceCodeFlow(manifest, client, descriptor.ProviderKey)
                : null;

        public bool SupportsCostFeed => descriptor.SupportsCostFeed;

        public string CostFeedPermissionDeniedNote =>
            descriptor.CostFeedPermissionDeniedNote
            ?? IProviderPlugin.DefaultCostFeedPermissionDeniedNote;

        public IProviderCostFeed? CreateCostFeed(ProviderClientOptions options) =>
            CreateCostFeed(options, string.Empty);

        public IProviderCostFeed? CreateCostFeed(
            ProviderClientOptions options,
            string credential) =>
            descriptor.SupportsCostFeed
                ? new ForeignModuleProviderCostFeed(
                    manifest,
                    client,
                    descriptor.ProviderKey,
                    credential)
                : null;

        public IProviderApiClient CreateClient(ProviderClientOptions options) =>
            CreateClient(options, string.Empty);

        public IProviderApiClient CreateClient(
            ProviderClientOptions options,
            string credential)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (RequiresEndpoint
                && !SupportsAutomaticEndpointDiscovery
                && string.IsNullOrWhiteSpace(options.Endpoint))
            {
                throw new ArgumentException(
                    $"Provider '{ProviderKey}' requires a non-empty endpoint URL.",
                    nameof(options));
            }

            return new ForeignModuleProviderApiClient(
                manifest,
                client,
                descriptor.ProviderKey,
                options.Endpoint,
                credential,
                descriptor.SupportsNativeToolCalling);
        }

        public Task<string> GetAgentIdentifierSuffixAsync(
            string providerName,
            Guid modelId,
            CancellationToken ct = default) =>
            client.GetProviderAgentIdentifierSuffixAsync(
                manifest,
                ProviderKey,
                providerName,
                modelId,
                ct);
    }

    private sealed class ForeignModuleProviderApiClient(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        string providerKey,
        string? endpoint,
        string apiKey,
        bool supportsNativeToolCalling) : IProviderApiClient
    {
        public string ProviderKey => providerKey;
        public bool SupportsNativeToolCalling => supportsNativeToolCalling;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            client.ListProviderModelIdsAsync(
                manifest,
                ProviderKey,
                endpoint,
                apiKey,
                ct);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default) =>
            client.CompleteProviderChatAsync(
                manifest,
                ProviderKey,
                endpoint,
                apiKey,
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
            client.CompleteProviderChatWithToolsAsync(
                manifest,
                ProviderKey,
                endpoint,
                apiKey,
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
            client.StreamProviderChatWithToolsAsync(
                manifest,
                ProviderKey,
                endpoint,
                apiKey,
                model,
                systemPrompt,
                messages,
                tools,
                maxCompletionTokens,
                providerParameters,
                completionParameters,
                ct);
    }

    private sealed class ForeignModuleModelCapabilityResolver(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        string providerKey) : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) =>
            client.ResolveProviderCapabilitiesAsync(
                manifest,
                providerKey,
                modelName,
                CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed class ForeignModuleDeviceCodeFlow(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        string providerKey) : IDeviceCodeFlow
    {
        public Task<DeviceCodeSession> StartAsync(CancellationToken ct = default) =>
            client.StartProviderDeviceCodeAsync(manifest, providerKey, ct);

        public Task<string?> PollAsync(
            DeviceCodeSession session,
            CancellationToken ct = default) =>
            client.PollProviderDeviceCodeAsync(manifest, providerKey, session, ct);
    }

    private sealed class ForeignModuleProviderCostFeed(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        string providerKey,
        string apiKey) : IProviderCostFeed
    {
        public Task<ProviderCostResult?> GetCostsAsync(
            DateTimeOffset startTime,
            DateTimeOffset? endTime,
            CancellationToken ct = default) =>
            client.GetProviderCostsAsync(
                manifest,
                providerKey,
                apiKey,
                startTime,
                endTime,
                ct);
    }

    private sealed class ForeignModuleCompletionParameterSpec(
        ForeignModuleCompletionParameterSpecDescriptor descriptor) : ICompletionParameterSpec
    {
        public string ProviderName => descriptor.ProviderName;
        public bool SupportsTemperature => descriptor.SupportsTemperature;
        public float TemperatureMin => descriptor.TemperatureMin;
        public float TemperatureMax => descriptor.TemperatureMax;
        public bool SupportsTopP => descriptor.SupportsTopP;
        public float TopPMin => descriptor.TopPMin;
        public float TopPMax => descriptor.TopPMax;
        public bool SupportsTopK => descriptor.SupportsTopK;
        public int TopKMin => descriptor.TopKMin;
        public int TopKMax => descriptor.TopKMax;
        public bool SupportsFrequencyPenalty => descriptor.SupportsFrequencyPenalty;
        public float FrequencyPenaltyMin => descriptor.FrequencyPenaltyMin;
        public float FrequencyPenaltyMax => descriptor.FrequencyPenaltyMax;
        public bool SupportsPresencePenalty => descriptor.SupportsPresencePenalty;
        public float PresencePenaltyMin => descriptor.PresencePenaltyMin;
        public float PresencePenaltyMax => descriptor.PresencePenaltyMax;
        public bool SupportsStop => descriptor.SupportsStop;
        public int MaxStopSequences => descriptor.MaxStopSequences;
        public bool SupportsSeed => descriptor.SupportsSeed;
        public bool SupportsResponseFormat => descriptor.SupportsResponseFormat;
        public bool RejectsJsonObjectResponseFormat => descriptor.RejectsJsonObjectResponseFormat;
        public bool OnlyJsonObjectResponseFormat => descriptor.OnlyJsonObjectResponseFormat;
        public bool SupportsReasoningEffort => descriptor.SupportsReasoningEffort;
        public bool ReasoningEffortInformationalOnly => descriptor.ReasoningEffortInformationalOnly;
        public string[] ValidReasoningEffortValues =>
            descriptor.ValidReasoningEffortValues
            ?? ["none", "minimal", "low", "medium", "high", "xhigh"];

        public bool SupportsToolChoice => descriptor.SupportsToolChoice;
        public bool SupportsStrictTools => descriptor.SupportsStrictTools;
    }

    private sealed class ForeignModuleProtocolContractInvoker(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleProtocolContractExport export) : IForeignModuleProtocolContractInvoker
    {
        public string ContractName => export.ContractName;
        public IReadOnlyList<ForeignModuleProtocolContractOperation> Operations => export.Operations;

        public async Task<JsonElement> InvokeAsync(
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            if (!Operations.Any(candidate => string.Equals(candidate.Name, operation, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Protocol contract '{ContractName}' does not define operation '{operation}'.");
            }

            var response = await client.InvokeProtocolContractAsync(
                manifest,
                ContractName,
                operation,
                parameters,
                ct);
            return response.Result;
        }
    }
}
