using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Core.Clients;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Modules;
using SharpClaw.Contracts.Modules.Foreign;
using SharpClaw.Providers.Common;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ForeignModuleProtocolContractTests
{
    [Test]
    public async Task ForeignModuleExportsProtocolContractAndInvoker()
    {
        using var workspace = TestWorkspace.Create();
        await using var foreignHost = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace));
        var registry = new ModuleRegistry();
        registry.Register(foreignHost.Module, foreignHost);
        using var parameters = JsonDocument.Parse("""{"path":"README.md"}""");

        var resolved = registry.ResolveProtocolContract("editor_bridge");
        var invoker = registry.ResolveProtocolContractInvoker("editor_bridge");
        var result = await invoker!.InvokeAsync(
            "open_file",
            parameters.RootElement,
            CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Value.ModuleId.Should().Be("sample_node_module");
        resolved.Value.Export.Operations.Should().Contain(operation => operation.Name == "open_file");
        ((IForeignModuleProtocolContractModule)foreignHost.Module).RequiredProtocolContracts.Should()
            .Contain(requirement => requirement.ContractName == "theme_bridge" && requirement.Optional);
        result.GetProperty("contractName").GetString().Should().Be("editor_bridge");
        result.GetProperty("operation").GetString().Should().Be("open_file");
        result.GetProperty("parameters").GetProperty("path").GetString().Should().Be("README.md");
    }

    [Test]
    public async Task ForeignModuleProxyExposesDiscoveredContributionDescriptors()
    {
        using var workspace = TestWorkspace.Create();
        await using var foreignHost = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace));
        var module = foreignHost.Module.Should().BeAssignableTo<ISharpClawRuntimeModule>().Subject;

        var headerTag = module.GetHeaderTags().Should().ContainSingle().Subject;
        var resource = module.GetResourceTypeDescriptors().Should().ContainSingle().Subject;
        var globalFlag = module.GetGlobalFlagDescriptors().Should().ContainSingle().Subject;
        var uiContribution = module.GetUiContributions().Should().ContainSingle().Subject;
        var frontendContribution = module.GetFrontendContributions().Should().ContainSingle().Subject;
        var cliCommand = module.GetCliCommands().Should().ContainSingle().Subject;

        headerTag.Name.Should().Be("sample_header");
        (await headerTag.Resolve(foreignHost.Services, CancellationToken.None))
            .Should()
            .Be("header:sample_header");

        resource.ResourceType.Should().Be("SampleResource");
        resource.DefaultResourceKey.Should().Be("sample");
        (await resource.LoadAllIds(foreignHost.Services, CancellationToken.None))
            .Should()
            .Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        (await resource.LoadLookupItems!(foreignHost.Services, CancellationToken.None))
            .Should()
            .ContainSingle(item => item.Id == Guid.Parse("11111111-1111-1111-1111-111111111111")
                                   && item.Name == "Sample One");

        globalFlag.FlagKey.Should().Be("CanUseSampleForeign");
        globalFlag.DelegateMethodName.Should().Be("UseSampleForeignAsync");
        uiContribution.ElementId.Should().Be("sample-sidecar");
        frontendContribution.Id.Should().Be("sample.settings");
        frontendContribution.Point.Should().Be(FrontendContributionPoint.SettingsPage);
        cliCommand.Name.Should().Be("sample");
        cliCommand.Aliases.Should().Equal("smp");
        cliCommand.Scope.Should().Be(ModuleCliScope.TopLevel);

        var parserAware = module.Should().BeAssignableTo<ITaskParserAware>().Which;
        var parserExtension = parserAware.ParserExtension;
        parserExtension.OperationKeyMappings["SampleTaskOperation"].OperationKey.Should().Be("sample.task.operation");
        parserExtension.OperationKeyMappings["SampleTaskOperation"].ModuleId.Should().Be("sample_node_module");
        parserExtension.EventTriggerMappings["OnSample"].TriggerKey.Should().Be("sample.trigger");
        parserExtension.SingleArgExpressionMethods.Should().Contain("SampleTaskOperation");

        var attributeContext = new TestTriggerAttributeContext(
            "SampleTrigger",
            12,
            stringArgs: ["positional"],
            intArgs: [null],
            rawArgs: ["\"positional\""],
            namedStringArgs: new Dictionary<string, string?> { ["Name"] = "named-sample" });
        var trigger = parserExtension.TriggerAttributeHandlers["SampleTrigger"].Handle(attributeContext);
        trigger.Should().NotBeNull();
        trigger!.TriggerKey.Should().Be("sample.trigger");
        trigger.Line.Should().Be(12);
        trigger.Parameters["name"].Should().Be("named-sample");
        attributeContext.Diagnostics.Should().BeEmpty();

        var taskServices = new ServiceCollection();
        module.ConfigureServices(taskServices);
        using var taskProvider = taskServices.BuildServiceProvider();

        var descriptorProvider = taskProvider.GetServices<ITaskOperationDescriptorProvider>()
            .Should()
            .ContainSingle()
            .Subject;
        descriptorProvider.ModuleId.Should().Be("sample_node_module");
        descriptorProvider.Descriptors.Should().ContainSingle(descriptor =>
            descriptor.OperationKey == "sample.task.operation"
            && descriptor.OwnerId == "sample_node_module"
            && descriptor.FirstArgIsExpression);

        var executor = taskProvider.GetServices<ITaskOperationExecutor>()
            .Should()
            .ContainSingle()
            .Subject;
        executor.CanExecute("sample.task.operation").Should().BeTrue();
        executor.Should().BeAssignableTo<ITaskOperationInvocationExecutor>();

        var executionContext = new TestTaskOperationExecutionContext();
        var shouldContinue = await executor.ExecuteAsync(
            "sample.task.operation",
            executionContext,
            ["alpha"],
            "expression",
            "operationResult");
        shouldContinue.Should().BeTrue();
        executionContext.Variables["sidecarOperation"].Should().Be("executed");
        executionContext.Variables["operationResult"].Should().Be("operation-result");
        executionContext.Logs.Should().Contain("operation log");
        executionContext.Outputs.Should().Contain("""{"sidecar":true}""");

        var invocationResult = await ((ITaskOperationInvocationExecutor)executor).ExecuteInvocationAsync(
            new TestTaskStatementInvocation("sample.task.operation")
            {
                ResultVariable = "invocationResult",
                RawExpression = "expression",
                Arguments = ["alpha"],
            },
            executionContext);
        invocationResult.Should().Be(TaskStatementResult.Continue);
        executionContext.Variables["sidecarInvocation"].Should().Be("executed");
        executionContext.Variables["invocationResult"].Should().Be("invocation-result");

        var triggerSource = taskProvider.GetServices<ITaskTriggerSource>()
            .Should()
            .ContainSingle()
            .Subject;
        var triggerDefinition = new TaskTriggerDefinition
        {
            TriggerKey = "sample.trigger",
            Line = 4,
            Parameters = new Dictionary<string, string?> { ["name"] = "sample" },
        };
        triggerSource.TriggerKeys.Should().Equal("sample.trigger");
        triggerSource.GetBindingValue(triggerDefinition).Should().Be("sample-value");
        triggerSource.GetBindingFilter(triggerDefinition).Should().Be("sample-filter");
        await triggerSource.StartAsync(
            [new TestTaskTriggerSourceContext(Guid.NewGuid(), triggerDefinition)],
            CancellationToken.None);
        (await triggerSource.SyncBindingsAsync(
            new TaskDefinitionDescriptor(Guid.NewGuid(), "sample-task"),
            [triggerDefinition],
            CancellationToken.None)).Should().BeTrue();
        await triggerSource.RemoveBindingsAsync(Guid.NewGuid(), CancellationToken.None);
        await triggerSource.StopAsync();

        var sideEffect = taskProvider.GetServices<ITaskTriggerBindingSideEffect>()
            .Should()
            .ContainSingle()
            .Subject;
        sideEffect.TriggerKey.Should().Be("sample.trigger");
        var binding = new TaskTriggerBindingDescriptor(
            Guid.NewGuid(),
            "sample.trigger",
            "sample-value",
            "sample-filter");
        await sideEffect.OnBindingCreatedAsync(
            new TaskDefinitionDescriptor(Guid.NewGuid(), "sample-task"),
            triggerDefinition,
            binding,
            CancellationToken.None);
        await sideEffect.OnBindingRemovedAsync(binding, CancellationToken.None);

        var metricProvider = taskProvider.GetServices<ITaskMetricProvider>()
            .Should()
            .ContainSingle()
            .Subject;
        metricProvider.MetricName.Should().Be("sample.metric");
        (await metricProvider.GetValueAsync(CancellationToken.None)).Should().Be(42.5);

        var eventSink = taskProvider.GetServices<ISharpClawEventSink>()
            .Should()
            .ContainSingle()
            .Subject;
        eventSink.SubscribedEvents.Should().Be(SharpClawEventType.AllModuleEvents);
        await eventSink.OnEventAsync(
            new SharpClawEvent(SharpClawEventType.ModuleEnabled, DateTimeOffset.UtcNow),
            CancellationToken.None);

        foreignHost.Services.GetServices<IProviderPlugin>()
            .Should()
            .ContainSingle(plugin => plugin.ProviderKey == "sample-foreign-provider");

        var providerPlugin = taskProvider.GetServices<IProviderPlugin>()
            .Should()
            .ContainSingle()
            .Subject;
        providerPlugin.ProviderKey.Should().Be("sample-foreign-provider");
        providerPlugin.DisplayName.Should().Be("Sample Foreign Provider");
        providerPlugin.OwnerModuleId.Should().Be("sample_node_module");
        providerPlugin.RequiresEndpoint.Should().BeTrue();
        providerPlugin.SupportsAutomaticEndpointDiscovery.Should().BeTrue();
        providerPlugin.IsSeedable.Should().BeTrue();
        providerPlugin.RequiresApiKey.Should().BeFalse();
        providerPlugin.CostSeeds.Should().ContainSingle(seed =>
            seed.ModelName == "sample-model"
            && seed.InputCostPerMillion == 1.25m
            && seed.OutputCostPerMillion == 2.50m);
        providerPlugin.ParameterSpec.ProviderName.Should().Be("Sample Foreign Provider");
        providerPlugin.ParameterSpec.TemperatureMax.Should().Be(1.0f);
        providerPlugin.ParameterSpec.SupportsTopK.Should().BeFalse();
        providerPlugin.ParameterSpec.ValidReasoningEffortValues.Should().Equal("none", "low", "medium");
        providerPlugin.ParameterSpec.SupportsStrictTools.Should().BeTrue();
        providerPlugin.DeviceCodeFlow.Should().NotBeNull();
        providerPlugin.SupportsCostFeed.Should().BeTrue();
        providerPlugin.CostFeedPermissionDeniedNote.Should().Be("Sample foreign provider requires billing access.");
        providerPlugin.Capabilities.Resolve("sample-model")
            .Should()
            .BeEquivalentTo([WellKnownCapabilityKeys.Chat, WellKnownCapabilityKeys.Vision]);
        (await providerPlugin.GetAgentIdentifierSuffixAsync(
            "Sample Foreign Provider",
            Guid.NewGuid(),
            CancellationToken.None)).Should().Be("sample-sidecar");

        var providerFactory = new ProviderApiClientFactory(taskProvider.GetServices<IProviderPlugin>());
        providerFactory.IsAvailable("sample-foreign-provider").Should().BeTrue();
        providerFactory.GetParameterSpec("sample-foreign-provider").SupportsStrictTools.Should().BeTrue();

        var registry = new ModuleRegistry();
        registry.Register(module, foreignHost);
        var registryProviderFactory = new ProviderApiClientFactory([], registry);
        registryProviderFactory.IsAvailable("sample-foreign-provider").Should().BeTrue();
        registryProviderFactory.GetPlugin("sample-foreign-provider")!.DisplayName.Should().Be("Sample Foreign Provider");

        var providerClient = ProviderCredentialBinding.CreateClient(
            providerPlugin,
            new ProviderClientOptions("http://127.0.0.1:9999"),
            "api-key");
        providerClient.ProviderKey.Should().Be("sample-foreign-provider");
        providerClient.SupportsNativeToolCalling.Should().BeTrue();

        (await providerClient.ListModelIdsAsync(CancellationToken.None))
            .Should()
            .Equal("sample-model", "sample-vision-model");

        var chatResult = await providerClient.ChatCompletionAsync(
            "sample-model",
            "system",
            [new ChatCompletionMessage("user", "hello")],
            maxCompletionTokens: 64,
            ct: CancellationToken.None);
        chatResult.Content.Should().Be("chat:sample-foreign-provider:sample-model:1");
        chatResult.Usage!.PromptTokens.Should().Be(3);
        chatResult.FinishReason.Should().Be(FinishReason.Stop);

        using var toolSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        var toolResult = await providerClient.ChatCompletionWithToolsAsync(
            "sample-model",
            "system",
            [ToolAwareMessage.User("hello")],
            [new ChatToolDefinition("sample_tool", "Sample tool.", toolSchema.RootElement.Clone())],
            ct: CancellationToken.None);
        toolResult.Content.Should().Be("tools:sample-foreign-provider:sample-model:1");
        toolResult.ToolCalls.Should().ContainSingle(call =>
            call.Id == "call-1"
            && call.Name == "sample_tool"
            && call.ArgumentsJson == """{"ok":true}""");
        toolResult.FinishReason.Should().Be(FinishReason.ToolCalls);

        var streamChunks = new List<ChatStreamChunk>();
        await foreach (var chunk in providerClient.StreamChatCompletionWithToolsAsync(
                           "sample-model",
                           "system",
                           [ToolAwareMessage.User("hello")],
                           [new ChatToolDefinition("sample_tool", "Sample tool.", toolSchema.RootElement.Clone())],
                           ct: CancellationToken.None))
        {
            streamChunks.Add(chunk);
        }

        streamChunks.Should().HaveCount(4);
        streamChunks[0].Delta.Should().Be("stream ");
        streamChunks[1].ToolCallDelta!.ArgumentsFragment.Should().Be("""{"ok":""");
        streamChunks[^1].Finished!.ToolCalls.Should().ContainSingle(call => call.Name == "sample_tool");

        var deviceSession = await providerPlugin.DeviceCodeFlow!.StartAsync(CancellationToken.None);
        deviceSession.UserCode.Should().Be("USER-CODE");
        (await providerPlugin.DeviceCodeFlow.PollAsync(deviceSession, CancellationToken.None))
            .Should()
            .Be("device-access-token");

        var costFeed = ProviderCredentialBinding.CreateCostFeed(
            providerPlugin,
            new ProviderClientOptions(null),
            "api-key");
        costFeed.Should().NotBeNull();
        var costs = await costFeed!.GetCostsAsync(
            DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-02T00:00:00Z"),
            CancellationToken.None);
        costs.Should().NotBeNull();
        costs!.TotalAmount.Should().Be(12.34m);
        costs.DailyBuckets.Should().ContainSingle(bucket => bucket.Amount == 12.34m);
    }

    [Test]
    public void ProtocolContractsParticipateInInitializationOrder()
    {
        var registry = new ModuleRegistry();
        var provider = new ProtocolProviderModule();
        var consumer = new ProtocolConsumerModule();

        registry.Register(consumer);
        registry.Register(provider);
        var order = registry.GetInitializationOrder(out var excluded);

        excluded.Should().BeEmpty();
        order.Should().ContainInOrder(provider.Id, consumer.Id);
    }

    [Test]
    public void MissingRequiredProtocolContractExcludesConsumerFromInitialization()
    {
        var registry = new ModuleRegistry();
        var consumer = new ProtocolConsumerModule();

        registry.Register(consumer);
        var order = registry.GetInitializationOrder(out var excluded);

        order.Should().BeEmpty();
        excluded.Should().ContainSingle()
            .Which.Should().Be((consumer.Id, "Unsatisfied contract(s): editor_bridge"));
        registry.GetUnsatisfiedProtocolRequirements(consumer.Id).Should()
            .ContainSingle(requirement => requirement.ContractName == "editor_bridge");
    }

    private static ModuleManifest Manifest() =>
        new(
            "sample_node_module",
            "Sample Node Module",
            "1.0.0",
            "snm",
            "dist/server.js",
            "0.0.0");

    private static ModuleManifestRuntimeInfo RuntimeInfo() =>
        new(ModuleManifestRuntimeInfo.Node, "dist/server.js");

    private static ForeignModuleHostLaunchOptions CreateLaunchOptions(TestWorkspace workspace)
    {
        var helperPath = ResolveSidecarHelperPath();
        return new ForeignModuleHostLaunchOptions
        {
            ExecutablePath = "dotnet",
            Arguments = [helperPath, "--mode", "normal"],
            WorkingDirectory = Path.GetDirectoryName(helperPath),
            ModuleDirectory = workspace.ModuleDir,
            ModuleDataDirectory = workspace.DataDir,
            ControlAddress = new Uri($"http://127.0.0.1:{GetFreeTcpPort()}"),
            ControlToken = "run-token",
            StartupTimeout = TimeSpan.FromSeconds(5),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            HostVersion = "0.1.0-beta",
            Environment = new Dictionary<string, string>
            {
                ["SHARPCLAW_TEST_TOOL_PREFIX"] = "snm",
            },
        };
    }

    private static ForeignModuleProtocolContractExport EditorBridgeExport() =>
        new(
            "editor_bridge",
            EmptyObjectSchema(),
            [
                new ForeignModuleProtocolContractOperation(
                    "open_file",
                    EmptyObjectSchema(),
                    EmptyObjectSchema())
            ]);

    private static JsonElement EmptyObjectSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return document.RootElement.Clone();
    }

    private static string ResolveSidecarHelperPath()
    {
        var root = ResolveRepoRoot();
        var configuration = Directory.GetParent(TestContext.CurrentContext.TestDirectory)!.Name;
        var helperPath = Path.Combine(
            root,
            "SharpClaw.Tests.ForeignSidecar",
            "bin",
            configuration,
            "net10.0",
            "SharpClaw.Tests.ForeignSidecar.dll");

        File.Exists(helperPath).Should().BeTrue(
            $"foreign sidecar helper must be built before tests run: '{helperPath}'");
        return helperPath;
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SharpClaw repository root.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed class TestTaskOperationExecutionContext : ITaskOperationExecutionContext
    {
        private readonly List<ITaskEventHandler> _eventHandlers = [];

        public Guid InstanceId { get; } = Guid.NewGuid();
        public Guid ChannelId { get; private set; } = Guid.NewGuid();
        public CancellationToken CancellationToken => CancellationToken.None;
        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();
        public IDictionary<string, object?> Variables { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public IReadOnlyList<ITaskEventHandler> EventHandlers => _eventHandlers;
        public List<string> Logs { get; } = [];
        public List<string?> Outputs { get; } = [];

        public string ResolveExpression(string expression) =>
            Variables.TryGetValue(expression, out var value)
                ? value?.ToString() ?? string.Empty
                : expression;

        public Task AppendLogAsync(string message)
        {
            Logs.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteOutputAsync(string? outputJson)
        {
            Outputs.Add(outputJson);
            return Task.CompletedTask;
        }

        public void SetChannelId(Guid channelId) => ChannelId = channelId;

        public Task<TaskStatementResult> ExecuteStatementsAsync(
            IReadOnlyList<ITaskStatementInvocation> steps,
            CancellationToken cancellationToken) =>
            Task.FromResult(TaskStatementResult.Continue);

        public bool EvaluateCondition(string? expression) =>
            bool.TryParse(expression, out var result) && result;

        public void RegisterEventHandler(
            string moduleTriggerKey,
            string? parameterName,
            IReadOnlyList<ITaskStatementInvocation> body) =>
            _eventHandlers.Add(new TestTaskEventHandler(moduleTriggerKey, parameterName));

        public Task WaitIfPausedAsync() => Task.CompletedTask;
    }

    private sealed record TestTaskEventHandler(
        string? ModuleTriggerKey,
        string? ParameterName) : ITaskEventHandler
    {
        public Task ExecuteBodyAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record TestTaskStatementInvocation(string StatementKey) : ITaskStatementInvocation
    {
        public string? VariableName { get; init; }
        public string? TypeName { get; init; }
        public string? ResultVariable { get; init; }
        public string? RawExpression { get; init; }
        public IReadOnlyList<string>? Arguments { get; init; }
        public string? ModuleTriggerKey { get; init; }
        public string? HandlerParameter { get; init; }
        public IReadOnlyList<ITaskStatementInvocation>? Body { get; init; }
        public IReadOnlyList<ITaskStatementInvocation>? ElseBody { get; init; }
    }

    private sealed record TestTaskTriggerSourceContext(
        Guid TaskDefinitionId,
        TaskTriggerDefinition Definition) : ITaskTriggerSourceContext
    {
        public Task FireAsync(
            IReadOnlyDictionary<string, string>? parameters = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TestTriggerAttributeContext : TaskTriggerAttributeContext
    {
        private readonly IReadOnlyList<string?> _stringArgs;
        private readonly IReadOnlyList<int?> _intArgs;
        private readonly IReadOnlyList<string?> _rawArgs;
        private readonly IReadOnlyDictionary<string, string?> _namedStringArgs;
        private readonly IReadOnlyDictionary<string, int?> _namedIntArgs;
        private readonly IReadOnlyDictionary<string, double?> _namedDoubleArgs;

        public TestTriggerAttributeContext(
            string attributeName,
            int line,
            IReadOnlyList<string?> stringArgs,
            IReadOnlyList<int?> intArgs,
            IReadOnlyList<string?> rawArgs,
            IReadOnlyDictionary<string, string?>? namedStringArgs = null,
            IReadOnlyDictionary<string, int?>? namedIntArgs = null,
            IReadOnlyDictionary<string, double?>? namedDoubleArgs = null)
        {
            AttributeName = attributeName;
            Line = line;
            _stringArgs = stringArgs;
            _intArgs = intArgs;
            _rawArgs = rawArgs;
            _namedStringArgs = namedStringArgs ?? new Dictionary<string, string?>(StringComparer.Ordinal);
            _namedIntArgs = namedIntArgs ?? new Dictionary<string, int?>(StringComparer.Ordinal);
            _namedDoubleArgs = namedDoubleArgs ?? new Dictionary<string, double?>(StringComparer.Ordinal);
        }

        public override string AttributeName { get; }
        public override int Line { get; }
        public override int ArgumentCount => Math.Max(_stringArgs.Count, Math.Max(_intArgs.Count, _rawArgs.Count));
        public List<(TaskTriggerAttributeDiagnosticSeverity Severity, string Code, string Message)> Diagnostics { get; } = [];

        public override string? GetStringArg(int index) =>
            index >= 0 && index < _stringArgs.Count ? _stringArgs[index] : null;

        public override int? GetIntArg(int index) =>
            index >= 0 && index < _intArgs.Count ? _intArgs[index] : null;

        public override string? GetNamedStringArg(string name) =>
            _namedStringArgs.TryGetValue(name, out var value) ? value : null;

        public override int? GetNamedIntArg(string name) =>
            _namedIntArgs.TryGetValue(name, out var value) ? value : null;

        public override double? GetNamedDoubleArg(string name) =>
            _namedDoubleArgs.TryGetValue(name, out var value) ? value : null;

        public override T? GetNamedEnumArg<T>(string name) where T : struct =>
            null;

        public override string? GetRawArgText(int index) =>
            index >= 0 && index < _rawArgs.Count ? _rawArgs[index] : null;

        public override void Report(
            TaskTriggerAttributeDiagnosticSeverity severity,
            string code,
            string message) =>
            Diagnostics.Add((severity, code, message));
    }

    private sealed class ProtocolProviderModule : ISharpClawCoreModule, IForeignModuleProtocolContractExporter
    {
        private readonly ForeignModuleProtocolContractExport _export = EditorBridgeExport();

        public string Id => "protocol_provider";
        public string DisplayName => "Protocol Provider";
        public string ToolPrefix => "pp";
        public IReadOnlyList<ForeignModuleProtocolContractExport> ExportedProtocolContracts => [_export];
        public IReadOnlyList<ForeignModuleProtocolContractRequirement> RequiredProtocolContracts => [];

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public IForeignModuleProtocolContractInvoker GetProtocolContractInvoker(string contractName) =>
            new StaticProtocolContractInvoker(_export);
    }

    private sealed class ProtocolConsumerModule : ISharpClawCoreModule, IForeignModuleProtocolContractModule
    {
        public string Id => "protocol_consumer";
        public string DisplayName => "Protocol Consumer";
        public string ToolPrefix => "pc";
        public IReadOnlyList<ForeignModuleProtocolContractExport> ExportedProtocolContracts => [];
        public IReadOnlyList<ForeignModuleProtocolContractRequirement> RequiredProtocolContracts =>
            [new("editor_bridge")];

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StaticProtocolContractInvoker(
        ForeignModuleProtocolContractExport export) : IForeignModuleProtocolContractInvoker
    {
        public string ContractName => export.ContractName;
        public IReadOnlyList<ForeignModuleProtocolContractOperation> Operations => export.Operations;

        public Task<JsonElement> InvokeAsync(
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            using var document = JsonDocument.Parse("""{"ok":true}""");
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root)
        {
            Root = root;
            ModuleDir = Path.Combine(root, "module");
            DataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(ModuleDir);
            Directory.CreateDirectory(DataDir);
        }

        public string Root { get; }
        public string ModuleDir { get; }
        public string DataDir { get; }

        public static TestWorkspace Create() =>
            new(Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "foreign-contracts",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
