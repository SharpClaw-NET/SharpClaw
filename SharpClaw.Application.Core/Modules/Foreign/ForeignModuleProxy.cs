using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Core.Modules.Foreign;

internal sealed class ForeignModuleProxy(
    ModuleManifest manifest,
    ForeignModuleProtocolClient client,
    Func<Task> shutdown)
    : ISharpClawModule, IForeignModuleProtocolContractExporter, ITaskParserAware
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
    private IReadOnlyList<ForeignModuleCliCommandDescriptor> _cliCommands = [];
    private ForeignModuleTaskParserDescriptor? _taskParser;
    private IReadOnlyList<TaskStepDescriptor> _taskStepDescriptors = [];
    private IReadOnlyList<ForeignModuleTaskStepExecutorDescriptor> _taskStepExecutors = [];
    private IReadOnlyList<ForeignModuleTaskTriggerSourceDescriptor> _taskTriggerSources = [];
    private IReadOnlyList<ForeignModuleTaskTriggerBindingSideEffectDescriptor> _taskTriggerBindingSideEffects = [];
    private IReadOnlyList<ForeignModuleTaskMetricProviderDescriptor> _taskMetricProviders = [];
    private IReadOnlyList<ForeignModuleTaskEventSinkDescriptor> _taskEventSinks = [];
    private ITaskParserModuleExtension? _parserExtension;

    public string Id => manifest.Id;
    public string DisplayName => manifest.DisplayName;
    public string ToolPrefix => manifest.ToolPrefix;

    public void ConfigureServices(IServiceCollection services)
    {
        if (_taskStepDescriptors.Count > 0)
        {
            services.AddSingleton<ITaskStepDescriptorProvider>(
                new ForeignModuleTaskStepDescriptorProvider(manifest.Id, _taskStepDescriptors));
        }

        foreach (var executor in _taskStepExecutors)
        {
            services.AddSingleton<ITaskStepExecutorExtension>(
                executor.SupportsInvocation
                    ? new ForeignModuleTaskStepInvocationExecutor(manifest, client, executor)
                    : new ForeignModuleTaskStepExecutor(manifest, client, executor));
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

    public IReadOnlyList<ModuleCliCommand>? GetCliCommands() =>
        [.. _cliCommands.Select(command => command.ToModuleCliCommand(manifest, client))];

    public IReadOnlyList<ForeignModuleProtocolContractExport> ExportedProtocolContracts =>
        [.. _protocolContracts.Select(contract => contract.ToProtocolContractExport())];

    public IReadOnlyList<ForeignModuleProtocolContractRequirement> RequiredProtocolContracts =>
        [.. _requiredProtocolContracts.Select(contract => contract.ToProtocolContractRequirement())];

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
        _cliCommands = discovery.CliCommands ?? [];
        _taskParser = discovery.TaskParser;
        _taskStepDescriptors = discovery.TaskStepDescriptors ?? [];
        _taskStepExecutors = discovery.TaskStepExecutors ?? [];
        _taskTriggerSources = discovery.TaskTriggerSources ?? [];
        _taskTriggerBindingSideEffects = discovery.TaskTriggerBindingSideEffects ?? [];
        _taskMetricProviders = discovery.TaskMetricProviders ?? [];
        _taskEventSinks = discovery.TaskEventSinks ?? [];
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
        AgentJobContext job) =>
        _tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))
            ?.CompletionBehavior
        ?? ModuleJobCompletionBehavior.CompleteWhenExecutionReturns;

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

    private static ForeignModuleTaskStepExecutionContextSnapshot SnapshotContext(
        ITaskStepExecutionContext context) =>
        new(
            context.InstanceId,
            context.ChannelId,
            SnapshotVariables(context.Variables),
            [.. context.EventHandlers.Select(handler =>
                new ForeignModuleTaskEventHandlerSnapshot(
                    handler.ModuleTriggerKey,
                    handler.ParameterName))]);

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

    private static async Task ApplyTaskStepResponseAsync(
        ForeignModuleTaskStepExecutionResponse response,
        ITaskStepExecutionContext context,
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
    }

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
        private readonly IReadOnlyDictionary<string, (string StepKey, string ModuleId)> _stepKeyMappings;
        private readonly IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> _eventTriggerMappings;
        private readonly IReadOnlySet<string> _singleArgExpressionMethods;
        private readonly IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> _triggerAttributeHandlers;

        public ForeignModuleTaskParserExtension(
            ModuleManifest manifest,
            ForeignModuleProtocolClient client,
            ForeignModuleTaskParserDescriptor? descriptor)
        {
            _stepKeyMappings = descriptor?.StepKeyMappings?.ToDictionary(
                mapping => mapping.MethodName,
                mapping => (mapping.StepKey, mapping.ModuleId),
                StringComparer.Ordinal)
                ?? new Dictionary<string, (string StepKey, string ModuleId)>(StringComparer.Ordinal);

            _eventTriggerMappings = descriptor?.EventTriggerMappings?.ToDictionary(
                mapping => mapping.MethodName,
                mapping => (mapping.TriggerKey, mapping.ModuleId),
                StringComparer.Ordinal)
                ?? new Dictionary<string, (string TriggerKey, string ModuleId)>(StringComparer.Ordinal);

            _singleArgExpressionMethods = descriptor?.SingleArgExpressionMethods?.ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);

            Primitives = descriptor?.Primitives;

            _triggerAttributeHandlers = descriptor?.TriggerAttributeHandlers?.ToDictionary(
                handler => handler.Name,
                handler => (ITaskTriggerAttributeHandler)new ForeignModuleTaskTriggerAttributeHandler(
                    manifest,
                    client,
                    handler),
                StringComparer.Ordinal)
                ?? new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings =>
            _stepKeyMappings;

        public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings =>
            _eventTriggerMappings;

        public IReadOnlySet<string> SingleArgExpressionMethods => _singleArgExpressionMethods;

        public TaskParserPrimitives? Primitives { get; }

        public IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers =>
            _triggerAttributeHandlers;
    }

    private sealed class ForeignModuleTaskTriggerAttributeHandler(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskTriggerAttributeHandlerDescriptor descriptor) : ITaskTriggerAttributeHandler
    {
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

    private sealed class ForeignModuleTaskStepDescriptorProvider(
        string moduleId,
        IReadOnlyList<TaskStepDescriptor> descriptors) : ITaskStepDescriptorProvider
    {
        public string ModuleId => moduleId;
        public IReadOnlyList<TaskStepDescriptor> Descriptors => descriptors;
    }

    private class ForeignModuleTaskStepExecutor(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskStepExecutorDescriptor descriptor) : ITaskStepExecutorExtension
    {
        private readonly HashSet<string> _stepKeys = new(descriptor.StepKeys, StringComparer.Ordinal);

        protected ModuleManifest Manifest { get; } = manifest;
        protected ForeignModuleProtocolClient Client { get; } = client;

        public string ModuleId => descriptor.ModuleId;

        public bool CanExecute(string moduleStepKey) => _stepKeys.Contains(moduleStepKey);

        public async Task<bool> ExecuteAsync(
            string moduleStepKey,
            ITaskStepExecutionContext context,
            IReadOnlyList<string>? arguments,
            string? expression,
            string? resultVariable)
        {
            var response = await Client.ExecuteTaskStepAsync(
                Manifest,
                moduleStepKey,
                SnapshotContext(context),
                arguments,
                expression,
                resultVariable,
                context.CancellationToken);

            await ApplyTaskStepResponseAsync(response, context, resultVariable);
            return response.Continue ?? response.Result == TaskStepResult.Continue;
        }
    }

    private sealed class ForeignModuleTaskStepInvocationExecutor(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client,
        ForeignModuleTaskStepExecutorDescriptor descriptor)
        : ForeignModuleTaskStepExecutor(manifest, client, descriptor), ITaskStepInvocationExecutor
    {
        public async Task<TaskStepResult> ExecuteInvocationAsync(
            ITaskStepInvocation step,
            ITaskStepExecutionContext context)
        {
            var response = await Client.ExecuteTaskStepInvocationAsync(
                Manifest,
                step,
                SnapshotContext(context),
                context.CancellationToken);

            await ApplyTaskStepResponseAsync(response, context, step.ResultVariable);
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
