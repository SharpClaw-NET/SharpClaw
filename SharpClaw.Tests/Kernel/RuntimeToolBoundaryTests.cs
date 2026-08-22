using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeToolBoundaryTests
{
    [Test]
    public void Tool_manifest_matches_the_published_catalog_without_local_keys()
    {
        var expected = SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith("tool.", StringComparison.Ordinal))
            .Select(static key => key.Value)
            .ToArray();

        RuntimeToolActionManifest.Required
            .Select(static key => key.Value)
            .Should()
            .Equal(expected);
    }

    [Test]
    public void Runtime_does_not_call_tool_handlers_outside_the_core_pipeline()
    {
        var sourceRoot = FindSourceRoot();
        var runtimeSources = Directory.EnumerateFiles(
                Path.Combine(sourceRoot, "SharpClaw.Runtime"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));

        var offenders = runtimeSources
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: index + 1, Text: line)))
            .Where(entry => entry.Text.Contains("IToolHandler", StringComparison.Ordinal))
            .Where(entry => !(
                string.Equals(
                    Path.GetFileName(entry.Path),
                    "RuntimeModuleStorageContractProvider.cs",
                    StringComparison.Ordinal)
                && entry.Text.Contains(
                    "where THandler : IToolHandler",
                    StringComparison.Ordinal)))
            .Select(entry => $"{entry.Path}:{entry.Line}")
            .ToArray();

        offenders.Should().BeEmpty(
            "Core UnifiedToolPipeline owns handler resolution and invocation");
    }

    [Test]
    public async Task Packaged_tool_handler_runs_through_core_actions_once()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new ToolProbe();
        var provider = new ToolProvider();
        var adapter = CreateAdapter(workspace, probe, provider);

        var result = await adapter.Kernel.RunAsync(new ChatTurnInput("use the tool"));

        result.Completion.Content.Should().Be("tool completed");
        probe.HandlerCalls.Should().Be(1);
        var hostContext = probe.HostContexts.Should().ContainSingle().Which;
        hostContext.IsWellFormed(DateTimeOffset.UtcNow).Should().BeTrue();
        hostContext.Ingress.Should().Be(HostActionEntryIngress.Tool);
        hostContext.Contribution.Should().NotBeNull();
        hostContext.Contribution!.IngressBinding.PrimaryIdentity.Should().Be("tool-boundary");
        probe.Observations
            .Select(static value => value.Action)
            .Should()
            .ContainInOrder(
                "tool.call.propose",
                "tool.definition.select",
                "tool.call.check",
                "tool.call.coordinate",
                "tool.handler.invoke",
                "tool.result.transform",
                "tool.result.return");
        provider.TransportCalls.Should().Be(2);
    }

    [TestCase("replace-input")]
    [TestCase("replace-result")]
    [TestCase("repeat")]
    public async Task Tool_pure_controls_keep_one_handler_execution(string mode)
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new ToolProbe { Mode = mode };
        var provider = new ToolProvider();
        var adapter = CreateAdapter(
            workspace,
            probe,
            provider,
            mode == "repeat" ? new MatchingRepeatEvidenceAuthority() : null);

        var result = await adapter.Kernel.RunAsync(new ChatTurnInput("use the tool"));

        result.Completion.Content.Should().Be(
            mode == "replace-result" ? "replaced tool result" : "tool completed");
        probe.HandlerCalls.Should().Be(1);
        if (mode == "replace-input")
            probe.LastValue.Should().Be(42);
        if (mode == "repeat")
            probe.Observations.Count(value => value.Action == "tool.call.input.transform")
                .Should().Be(2);
    }

    [TestCase("cancel")]
    [TestCase("fail")]
    public async Task Tool_action_cancellation_or_failure_stops_before_handler(string mode)
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new ToolProbe { Mode = mode };
        var provider = new ToolProvider();
        var adapter = CreateAdapter(workspace, probe, provider);

        Func<Task> run = async () => await adapter.Kernel.RunAsync(
            new ChatTurnInput("blocked tool", Guid.NewGuid()));
        if (mode == "cancel")
            await run.Should().ThrowAsync<KernelActionCancelledException>();
        else
            await run.Should().ThrowAsync<KernelActionFailedException>();

        probe.HandlerCalls.Should().Be(0);
        probe.Observations.Should().Contain(value =>
            value.Action == $"tool.call.{mode}");
        provider.TransportCalls.Should().Be(1);
    }

    [Test]
    public async Task Tool_actions_preserve_distinct_request_contexts()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new ToolProbe();
        var provider = new ToolProvider();
        var adapter = CreateAdapter(workspace, probe, provider);

        var first = adapter.RunRequestAsync(
            Context("tool-user-a"),
            new ChatTurnInput("first", Guid.NewGuid()),
            (input, ct) => adapter.Kernel.RunAsync(input, ct));
        var second = adapter.RunRequestAsync(
            Context("tool-user-b"),
            new ChatTurnInput("second", Guid.NewGuid()),
            (input, ct) => adapter.Kernel.RunAsync(input, ct));

        await Task.WhenAll(first.AsTask(), second.AsTask());

        probe.Observations
            .Where(static value => value.Action.StartsWith(
                "tool.", StringComparison.Ordinal))
            .Should()
            .NotBeEmpty()
            .And.AllSatisfy(value =>
            {
                new[] { "tool-user-a", "tool-user-b" }
                    .Should()
                    .Contain(value.Subject);
                value.Depth.Should().BeGreaterThan(0);
                value.ParentInvocationId.Should().NotBeNull();
            });
        probe.Observations.Should().Contain(value =>
            value.Action == "tool.handler.invoke" && value.Subject == "tool-user-a");
        probe.Observations.Should().Contain(value =>
            value.Action == "tool.handler.invoke" && value.Subject == "tool-user-b");
        probe.HostContexts
            .Select(static value => value.Caller.SubjectId)
            .Should()
            .Contain("tool-user-a")
            .And.Contain("tool-user-b");
    }

    private static RuntimeKernelAdapter CreateAdapter(
        TemporaryWorkspace workspace,
        ToolProbe probe,
        ToolProvider provider,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null)
    {
        var moduleId = "tool-boundary-test";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = provider.ProviderKey,
                ["Provider:Model"] = "tool-model",
            })
            .Build();
        var grants = RuntimeToolActionManifest.Required
            .Concat(RuntimeProviderActionManifest.Required)
            .ToDictionary(
                key => key.Value,
                key => KernelActionCatalog.DescriptorFor(key).Capabilities,
                StringComparer.Ordinal);
        var options = new KernelGraphCompileOptions
        {
            ActionModuleCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
            {
                [moduleId] = grants,
            },
            SensitiveActionApprovals = RuntimeProviderActionManifest.Required
                .Select(key =>
                {
                    var descriptor = KernelActionCatalog.DescriptorFor(key).ToDescriptor();
                    var types = KernelSchemaIdentity.ActionTypes(
                        descriptor,
                        typeof(KernelActionEnvelope),
                        typeof(object));
                    return new KernelSensitiveActionApproval(
                        moduleId,
                        key,
                        descriptor.Version,
                        types.ActionType.AssemblyQualifiedName!,
                        types.ResultType.AssemblyQualifiedName!,
                        KernelSchemaIdentity.Action(
                            descriptor,
                            typeof(KernelActionEnvelope),
                            typeof(object)));
                })
                .ToArray(),
        };

        return new RuntimeKernelAdapter(
            configuration,
            new ServiceCollection().BuildServiceProvider(),
            [new ToolModule(moduleId, probe, provider)],
            workspace.Paths,
            new ToolProviderFactory(provider),
            options,
            repeatEvidenceAuthority);
    }

    private static KernelActionExecutionContext Context(string subject) =>
        new(
            new RequestPrincipal(
                subject,
                subject,
                new HashSet<string>(StringComparer.Ordinal),
                true),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

    private static string FindSourceRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot) &&
            Directory.Exists(Path.Combine(configuredRoot, "SharpClaw.Runtime")))
            return configuredRoot;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "SharpClaw.Runtime")))
                return directory.FullName;
        }

        throw new AssertionException("The SharpClaw source root could not be located.");
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-tool-boundary-" + Guid.NewGuid().ToString("N"));

        public TemporaryWorkspace()
        {
            Directory.CreateDirectory(_root);
            Paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                _root,
                _root,
                _root);
            Paths.EnsureDirectories();
        }

        public SharpClawInstancePaths Paths { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class ToolModule(
        string moduleId,
        ToolProbe probe,
        ToolProvider provider) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new(moduleId, "Tool boundary test", "tool-boundary");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton(probe);
            module.Services.AddSingleton(provider);
            module.Services.AddSingleton<ToolInterceptor>();
            module.Services.AddSingleton<ToolHandler>();
            module.Services.AddSingleton<IProviderPlugin>(provider);
            module.Tools.Add<ToolHandler>(new ToolDescriptor(
                "tool-boundary",
                "Runs the K08 boundary test tool.",
                ToolSchemas.EmptyObject));
            foreach (var action in RuntimeToolActionManifest.Required.Concat(
                         RuntimeProviderActionManifest.Required))
            {
                module.Hooks.For(action).Use<ToolInterceptor>(new HookOrdering(
                    $"tool-boundary-{action.Value}",
                    HookPriority.Normal,
                    [],
                    [],
                    TimeSpan.FromSeconds(5),
                    HookFailurePolicy.FailAction));
            }
        }

        public ValueTask StartAsync(
            ModuleStartContext context,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class ToolInterceptor(ToolProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.Observations.Enqueue(new ToolObservation(
                context.ActionKey.Value,
                context.Caller.SubjectId,
                context.Depth,
                context.ParentInvocationId));

            if (context.ActionKey.Value == "tool.call.check")
            {
                if (probe.Mode == "cancel")
                    return control.Cancel(
                        "TOOL_TEST_CANCELLED",
                        "Tool action cancelled.");
                if (probe.Mode == "fail")
                    return control.Fail(new ExecutionError(
                        "TOOL_TEST_FAILED",
                        "Tool action failed."));
            }

            if (context.ActionKey.Value == "tool.call.input.transform" &&
                context.Action.Payload is ToolInvocation invocation)
            {
                if (probe.Mode == "repeat" && context.Attempt == 1)
                {
                    return await control.RepeatAsync(
                        new ActionRepeatRequest<KernelActionEnvelope>(
                            context.Action,
                            "Tool pure action repeat.",
                            null),
                        cancellationToken);
                }

                if (probe.Mode == "replace-input")
                {
                    using var document = JsonDocument.Parse("{\"value\":42}");
                    return await control.ProceedWithInputAsync(
                        new ActionReplacement<KernelActionEnvelope>(
                            context.Action with
                            {
                                Payload = invocation with
                                {
                                    Arguments = document.RootElement.Clone(),
                                },
                            },
                            "Replace tool input."),
                        cancellationToken);
                }
            }

            if (probe.Mode == "replace-result" &&
                context.ActionKey.Value == "tool.result.transform")
            {
                return control.ReplaceResult(
                    ToolResult.Text("replaced tool result"),
                    "Replace tool result.");
            }

            return await control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ToolHandler(ToolProbe probe) : IToolHandler
    {
        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref probe.HandlerCalls);
            probe.HostContexts.Enqueue(invocation.HostActionContext);
            using var document = JsonDocument.Parse(invocation.Arguments.GetRawText());
            probe.LastValue = document.RootElement.TryGetProperty("value", out var value)
                ? value.GetInt32()
                : null;
            return ValueTask.FromResult(ToolResult.Text("tool completed"));
        }
    }

    private sealed class ToolProbe
    {
        public ConcurrentQueue<ToolObservation> Observations { get; } = new();
        public ConcurrentQueue<HostActionEntryRequestContext> HostContexts { get; } = new();
        public string? Mode { get; init; }
        public int HandlerCalls;
        public int? LastValue;
    }

    private sealed record ToolObservation(
        string Action,
        string Subject,
        int Depth,
        Guid? ParentInvocationId);

    private sealed class ToolProvider : IProviderPlugin, IProviderApiClient
    {
        private int _transportCalls;

        public string ProviderKey => "tool-boundary";
        public string DisplayName => "Tool boundary provider";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public bool SupportsNativeToolCalling => true;
        public IModelCapabilityResolver Capabilities { get; } = new EmptyCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public int TransportCalls => _transportCalls;

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["tool-model"]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken cancellationToken = default) =>
            CompleteAsync(
                messages.LastOrDefault(message =>
                    string.Equals(message.Role, "tool", StringComparison.Ordinal))?.Content,
                cancellationToken);

        public Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ToolAwareMessage> messages,
            IReadOnlyList<ChatToolDefinition> tools,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken cancellationToken = default) =>
            CompleteAsync(
                messages.LastOrDefault(message =>
                    string.Equals(message.Role, "tool", StringComparison.Ordinal))?.Content,
                cancellationToken);

        private Task<ChatCompletionResult> CompleteAsync(
            string? toolResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _transportCalls);
            if (toolResult is not null)
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Content = toolResult,
                    FinishReason = FinishReason.Stop,
                    Usage = new TokenUsage(call, 1),
                });
            }

            return Task.FromResult(new ChatCompletionResult
            {
                ToolCalls =
                [
                    new ChatToolCall(
                        "tool-call-1",
                        "tool-boundary",
                        "{\"value\":1}")
                ],
                FinishReason = FinishReason.ToolCalls,
                Usage = new TokenUsage(call, 1),
            });
        }
    }

    private sealed class ToolProviderFactory(IProviderApiClient provider)
        : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins) => provider;
    }

    private sealed class EmptyCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }

    private sealed class MatchingRepeatEvidenceAuthority
        : IKernelActionRepeatEvidenceAuthority
    {
        public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
            KernelActionRepeatEvidenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<KernelActionRepeatEvidence?>(new(
                Guid.NewGuid().ToString("N"),
                request.RequiredKind,
                request.ActionKey,
                request.ActionVersion,
                request.IdempotencyScope,
                request.IdempotencyKey,
                request.PriorInvocationId,
                request.PriorAttempt,
                request.NextInvocationId,
                request.NextAttempt,
                request.RequestedAt,
                request.RequestedAt.AddMinutes(1)));
        }
    }
}
