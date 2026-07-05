using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Parsing;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Contracts.Modules.Foreign;

namespace SharpClaw.Runtime.BLL.Modules.Foreign;

internal sealed class ForeignModuleProxy(
    ModuleManifest manifest,
    ForeignModuleProtocolClient client,
    Func<Task> shutdown)
    : ISharpClawRuntimeModule, IForeignModuleProtocolContractExporter, ITaskParserAware
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
    private ForeignModuleTaskParserDescriptor? _taskParser;
    private IReadOnlyList<TaskOperationDescriptor> _taskOperationDescriptors = [];
    private IReadOnlyList<ForeignModuleTaskOperationExecutorDescriptor> _taskOperationExecutors = [];
    private IReadOnlyList<ForeignModuleTaskTriggerSourceDescriptor> _taskTriggerSources = [];
    private IReadOnlyList<ForeignModuleTaskTriggerBindingSideEffectDescriptor> _taskTriggerBindingSideEffects = [];
    private IReadOnlyList<ForeignModuleTaskMetricProviderDescriptor> _taskMetricProviders = [];
    private IReadOnlyList<ForeignModuleTaskEventSinkDescriptor> _taskEventSinks = [];
    private IReadOnlyList<ForeignModuleProviderPluginDescriptor> _providerPlugins = [];
    private ITaskParserModuleExtension? _parserExtension;

    public string Id => manifest.Id;
    public string DisplayName => manifest.DisplayName;
    public string ToolPrefix => manifest.ToolPrefix;

    public void ConfigureServices(IServiceCollection services)
    {
        if (_taskOperationDescriptors.Count > 0)
        {
            services.AddSingleton<ITaskOperationDescriptorProvider>(
                new ForeignModuleTaskOperationDescriptorProvider(manifest.Id, _taskOperationDescriptors));
        }

        foreach (var executor in _taskOperationExecutors)
        {
            services.AddSingleton<ITaskOperationExecutor>(
                executor.SupportsInvocation
                    ? new ForeignModuleTaskOperationInvocationExecutor(manifest, client, executor)
                    : new ForeignModuleTaskOperationExecutor(manifest, client, executor));
        }

        foreach (var source in _taskTriggerSources)
        {
            services.AddSingleton<ITaskTriggerSource>(
                new ForeignModuleTaskTriggerSource(manifest, client, source));
        }

        foreach (var sideEffect in _taskTriggerBindingSideEffects)
        {
            services.AddSingleton<ITaskTriggerBindingSideEffect>(
                new ForeignModuleTaskTriggerBindingSideEffect(manifest, client, sideEffect));
        }

        foreach (var metricProvider in _taskMetricProviders)
        {
            services.AddSingleton<ITaskMetricProvider>(
                new ForeignModuleTaskMetricProvider(manifest, client, metricProvider));
        }

        foreach (var eventSink in _taskEventSinks)
        {
            services.AddSingleton<ISharpClawEventSink>(
                new ForeignModuleTaskEventSink(manifest, client, eventSink));
        }

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

    internal IReadOnlyList<TaskOperationDescriptor> TaskOperationDescriptors => _taskOperationDescriptors;

    public ITaskParserModuleExtension ParserExtension =>
        _parserExtension ??= new ForeignModuleTaskParserExtension(manifest, client, _taskParser);

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
        _taskParser = discovery.TaskParser;
        _taskOperationDescriptors = discovery.TaskOperationDescriptors ?? [];
        _taskOperationExecutors = discovery.TaskOperationExecutors ?? [];
        _taskTriggerSources = discovery.TaskTriggerSources ?? [];
        _taskTriggerBindingSideEffects = discovery.TaskTriggerBindingSideEffects ?? [];
        _taskMetricProviders = discovery.TaskMetricProviders ?? [];
        _taskEventSinks = discovery.TaskEventSinks ?? [];
        _providerPlugins = discovery.ProviderPlugins ?? [];
        _parserExtension = null;
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

    private static ForeignModuleTaskContextRegistry.ForeignModuleTaskContextRegistration? CreateTaskContextRegistration(
        ITaskOperationExecutionContext context) =>
        context.Services.GetService<ForeignModuleTaskContextRegistry>()?.Register(context);

    private static ForeignModuleTaskOperationExecutionContextSnapshot SnapshotContext(
        ITaskOperationExecutionContext context,
        ForeignModuleTaskContextRegistry.ForeignModuleTaskContextRegistration? registration) =>
        new(
            context.InstanceId,
            context.ChannelId,
            SnapshotVariables(context.Variables),
            [.. context.EventHandlers.Select(handler =>
                new ForeignModuleTaskEventHandlerSnapshot(
                    handler.ModuleTriggerKey,
                    handler.ParameterName,
                    registration?.RegisterEventHandler(handler)))],
            registration?.ContextId);

    private static IReadOnlyDictionary<string, JsonElement> SnapshotVariables(
        IDictionary<string, object?> variables)
    {
        var snapshot = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in variables)
            snapshot[key] = SerializeVariableValue(value);
        return snapshot;
    }

    private static JsonElement SerializeVariableValue(object? value)
    {
        if (value is JsonElement element)
            return element.Clone();

        try
        {
            return value is null
                ? JsonSerializer.SerializeToElement((string?)null, JsonOptions)
                : JsonSerializer.SerializeToElement(value, value.GetType(), JsonOptions);
        }
        catch (NotSupportedException)
        {
            return JsonSerializer.SerializeToElement(value?.ToString(), JsonOptions);
        }
    }

    private static async Task ApplyTaskOperationResponseAsync(
        ForeignModuleTaskOperationExecutionResponse response,
        ITaskOperationExecutionContext context,
        string? resultVariable)
    {
        if (response.ChannelId is { } channelId)
            context.SetChannelId(channelId);

        if (response.VariableUpdates is not null)
        {
            foreach (var (key, value) in response.VariableUpdates)
                context.Variables[key] = ConvertJsonValue(value);
        }

        if (!string.IsNullOrWhiteSpace(resultVariable)
            && response.ResultVariableValue is { } resultVariableValue)
        {
            context.Variables[resultVariable] = ConvertJsonValue(resultVariableValue);
        }

        if (response.Logs is not null)
        {
            foreach (var log in response.Logs)
                await context.AppendLogAsync(log);
        }

        if (response.OutputJson is not null)
            await context.WriteOutputAsync(response.OutputJson);

        if (response.RegisteredEventHandlers is not null)
        {
            foreach (var handler in response.RegisteredEventHandlers)
            {
                context.RegisterEventHandler(
                    handler.ModuleTriggerKey,
                    handler.ParameterName,
                    [.. handler.Body.Select(ToTaskStatementDefinition)]);
            }
        }
    }

    internal static TaskStatementDefinition ToTaskStatementDefinition(
        ForeignModuleTaskStatementInvocationDescriptor descriptor) =>
        new()
        {
            StatementKey = descriptor.StatementKey,
            Line = 0,
            Column = 0,
            VariableName = descriptor.VariableName,
            TypeName = descriptor.TypeName,
            ResultVariable = descriptor.ResultVariable,
            Expression = descriptor.RawExpression,
            Arguments = descriptor.Arguments,
            ModuleTriggerKey = descriptor.ModuleTriggerKey,
            HandlerParameter = descriptor.HandlerParameter,
            Body = descriptor.Body is null ? null : [.. descriptor.Body.Select(ToTaskStatementDefinition)],
            ElseBody = descriptor.ElseBody is null ? null : [.. descriptor.ElseBody.Select(ToTaskStatementDefinition)],
        };

    private static object? ConvertJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number => value.GetDouble(),
            _ => value.Clone(),
        };

    private sealed class ForeignModuleTaskParserExtension : ITaskParserModuleExtension
    {
        private readonly IReadOnlyDictionary<string, (string OperationKey, string ModuleId)> _operationKeyMappings;
        private readonly IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> _eventTriggerMappings;
        private readonly IReadOnlySet<string> _singleArgExpressionMethods;
        private readonly IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> _triggerAttributeHandlers;

        public ForeignModuleTaskParserExtension(
            ModuleManifest manifest,
            ForeignModuleProtocolClient client,
            ForeignModuleTaskParserDescriptor? descriptor)
        {
            _operationKeyMappings = descriptor?.OperationKeyMappings?.ToDictionary(
                mapping => mapping.MethodName,
                mapping => (mapping.StatementKey, mapping.ModuleId),
                StringComparer.Ordinal)
                ?? new Dictionary<string, (string OperationKey, string ModuleId)>(StringComparer.Ordinal);

            _eventTriggerMappings = descriptor?.EventTriggerMappings?.ToDictionary(
                mapping => mapping.MethodName,
                mapping => (mapping.TriggerKey, mapping.ModuleId),
                StringComparer.Ordinal)
                ?? new Dictionary<string, (string TriggerKey, string ModuleId)>(StringComparer.Ordinal);

            _singleArgExpressionMethods = descriptor?.SingleArgExpressionMethods?.ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);

            _triggerAttributeHandlers = descriptor?.TriggerAttributeHandlers?.ToDictionary(
                handler => handler.Name,
                handler => (ITaskTriggerAttributeHandler)new ForeignModuleTaskTriggerAttributeHandler(
                    manifest,
                    client,
                    handler),
                StringComparer.Ordinal)
                ?? new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, (string OperationKey, string ModuleId)> OperationKeyMappings =>
            _operationKeyMappings;

        public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings =>
            _eventTriggerMappings;

        public IReadOnlySet<string> SingleArgExpressionMethods => _singleArgExpressionMethods;

        public IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers =>
            _triggerAttributeHandlers;
    }

    private sealed class ForeignModuleTaskTriggerAttributeHandler(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskTriggerAttributeHandlerDescriptor descriptor)
        : ITaskTriggerAttributeHandler, ITaskTriggerAttributeHandlerOwnerHint
    {
        public string? TriggerAttributeOwnerKey =>
            string.IsNullOrWhiteSpace(manifest.EntryAssembly)
                ? manifest.Id
                : Path.GetFileNameWithoutExtension(manifest.EntryAssembly);

        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var response = client.HandleTaskTriggerAttributeAsync(
                manifest,
                descriptor.Name,
                CreateContextDescriptor(context),
                CancellationToken.None).GetAwaiter().GetResult();

            if (response.Diagnostics is not null)
            {
                foreach (var diagnostic in response.Diagnostics)
                {
                    context.Report(
                        diagnostic.Severity,
                        diagnostic.Code,
                        diagnostic.Message);
                }
            }

            return response.Trigger;
        }

        private ForeignModuleTaskTriggerAttributeContextDescriptor CreateContextDescriptor(
            TaskTriggerAttributeContext context) =>
            new(
                context.AttributeName,
                context.Line,
                context.ArgumentCount,
                [.. Enumerable.Range(0, context.ArgumentCount).Select(context.GetStringArg)],
                [.. Enumerable.Range(0, context.ArgumentCount).Select(context.GetIntArg)],
                [.. Enumerable.Range(0, context.ArgumentCount).Select(context.GetRawArgText)],
                descriptor.NamedStringArgs?.ToDictionary(
                    name => name,
                    context.GetNamedStringArg,
                    StringComparer.Ordinal)
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                descriptor.NamedIntArgs?.ToDictionary(
                    name => name,
                    context.GetNamedIntArg,
                    StringComparer.Ordinal)
                    ?? new Dictionary<string, int?>(StringComparer.Ordinal),
                descriptor.NamedDoubleArgs?.ToDictionary(
                    name => name,
                    context.GetNamedDoubleArg,
                    StringComparer.Ordinal)
                    ?? new Dictionary<string, double?>(StringComparer.Ordinal));
    }

    private sealed class ForeignModuleTaskOperationDescriptorProvider(
        string moduleId,
        IReadOnlyList<TaskOperationDescriptor> descriptors) : ITaskOperationDescriptorProvider
    {
        public string ModuleId => moduleId;
        public IReadOnlyList<TaskOperationDescriptor> Descriptors => descriptors;
    }

    private class ForeignModuleTaskOperationExecutor(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskOperationExecutorDescriptor descriptor) : ITaskOperationExecutor
    {
        private readonly HashSet<string> _operationKeys = new(descriptor.OperationKeys, StringComparer.Ordinal);

        protected ModuleManifest Manifest { get; } = manifest;
        protected ForeignModuleProtocolClient Client { get; } = client;

        public string ModuleId => descriptor.ModuleId;

        public bool CanExecute(string operationKey) => _operationKeys.Contains(operationKey);

        public async Task<bool> ExecuteAsync(
            string operationKey,
            ITaskOperationExecutionContext context,
            IReadOnlyList<string>? arguments,
            string? expression,
            string? resultVariable)
        {
            using var registration = CreateTaskContextRegistration(context);
            var response = await Client.ExecuteTaskOperationAsync(
                Manifest,
                operationKey,
                SnapshotContext(context, registration),
                arguments,
                expression,
                resultVariable,
                context.CancellationToken);

            await ApplyTaskOperationResponseAsync(response, context, resultVariable);
            return response.Continue ?? response.Result == TaskStatementResult.Continue;
        }
    }

    private sealed class ForeignModuleTaskOperationInvocationExecutor(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskOperationExecutorDescriptor descriptor)
        : ForeignModuleTaskOperationExecutor(manifest, client, descriptor), ITaskOperationInvocationExecutor
    {
        public async Task<TaskStatementResult> ExecuteInvocationAsync(
            ITaskStatementInvocation step,
            ITaskOperationExecutionContext context)
        {
            using var registration = CreateTaskContextRegistration(context);
            var response = await Client.ExecuteTaskOperationInvocationAsync(
                Manifest,
                step,
                SnapshotContext(context, registration),
                context.CancellationToken);

            await ApplyTaskOperationResponseAsync(response, context, step.ResultVariable);
            return response.Result;
        }
    }

    private sealed class ForeignModuleTaskTriggerSource(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskTriggerSourceDescriptor descriptor) : ITaskTriggerSource
    {
        public IReadOnlyList<string> TriggerKeys => descriptor.TriggerKeys;
        public bool OwnsBindingPersistence => descriptor.OwnsBindingPersistence;

        public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct) =>
            client.StartTaskTriggerSourceAsync(
                manifest,
                TriggerKeys,
                [.. contexts.Select(context =>
                    new ForeignModuleTaskTriggerSourceContextDescriptor(
                        context.TaskDefinitionId,
                        context.Definition))],
                ct);

        public Task StopAsync() =>
            client.StopTaskTriggerSourceAsync(
                manifest,
                TriggerKeys,
                CancellationToken.None);

        public string? GetBindingValue(TaskTriggerDefinition def) =>
            client.GetTaskTriggerBindingValueAsync(
                manifest,
                ResolveTriggerKey(def),
                def,
                CancellationToken.None).GetAwaiter().GetResult();

        public string? GetBindingFilter(TaskTriggerDefinition def) =>
            client.GetTaskTriggerBindingFilterAsync(
                manifest,
                ResolveTriggerKey(def),
                def,
                CancellationToken.None).GetAwaiter().GetResult();

        public Task<bool> SyncBindingsAsync(
            TaskDefinitionDescriptor definition,
            IReadOnlyList<TaskTriggerDefinition> ownedTriggers,
            CancellationToken ct) =>
            client.SyncTaskTriggerBindingsAsync(
                manifest,
                TriggerKeys,
                definition,
                ownedTriggers,
                ct);

        public Task RemoveBindingsAsync(Guid definitionId, CancellationToken ct) =>
            client.RemoveTaskTriggerBindingsAsync(
                manifest,
                TriggerKeys,
                definitionId,
                ct);

        private string ResolveTriggerKey(TaskTriggerDefinition definition) =>
            definition.TriggerKey is { Length: > 0 } triggerKey
                ? triggerKey
                : TriggerKeys.FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        $"Foreign module '{manifest.Id}' trigger source does not declare any trigger keys.");
    }

    private sealed class ForeignModuleTaskTriggerBindingSideEffect(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskTriggerBindingSideEffectDescriptor descriptor) : ITaskTriggerBindingSideEffect
    {
        public string TriggerKey => descriptor.TriggerKey;

        public Task OnBindingCreatedAsync(
            TaskDefinitionDescriptor definition,
            TaskTriggerDefinition trigger,
            TaskTriggerBindingDescriptor binding,
            CancellationToken ct) =>
            client.NotifyTaskTriggerBindingCreatedAsync(
                manifest,
                TriggerKey,
                definition,
                trigger,
                binding,
                ct);

        public Task OnBindingRemovedAsync(
            TaskTriggerBindingDescriptor binding,
            CancellationToken ct) =>
            client.NotifyTaskTriggerBindingRemovedAsync(
                manifest,
                TriggerKey,
                binding,
                ct);
    }

    private sealed class ForeignModuleTaskMetricProvider(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskMetricProviderDescriptor descriptor) : ITaskMetricProvider
    {
        public string MetricName => descriptor.MetricName;
        public string Description => descriptor.Description;

        public Task<double> GetValueAsync(CancellationToken ct) =>
            client.GetTaskMetricValueAsync(manifest, MetricName, ct);
    }

    private sealed class ForeignModuleTaskEventSink(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskEventSinkDescriptor descriptor) : ISharpClawEventSink
    {
        public SharpClawEventType SubscribedEvents => descriptor.SubscribedEvents;

        public Task OnEventAsync(SharpClawEvent evt, CancellationToken ct) =>
            client.SendTaskEventAsync(manifest, evt, ct);
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
