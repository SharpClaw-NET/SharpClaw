using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeProviderBoundaryTests
{
    [Test]
    public void Provider_transport_calls_are_confined_to_the_terminal_adapter()
    {
        var sourceRoot = FindSourceRoot();
        var kernelRoot = Path.Combine(sourceRoot, "SharpClaw.Runtime", "BLL", "Kernel");
        var allowed = Path.GetFullPath(Path.Combine(kernelRoot, "DirectChatKernel.cs"));
        var methodNames = new[]
        {
            "ChatCompletionAsync",
            "ChatCompletionWithToolsAsync",
            "StreamChatCompletionWithToolsAsync",
        };
        var offenders = Directory.EnumerateFiles(kernelRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), allowed, StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: index + 1, Text: line)))
            .Where(entry => methodNames.Any(name => entry.Text.Contains(name + "(", StringComparison.Ordinal)))
            .Select(entry => $"{entry.Path}:{entry.Line}")
            .ToArray();

        offenders.Should().BeEmpty("provider transport calls must stay in ProviderKernelTransport");
    }

    [Test]
    public void Provider_manifest_matches_the_published_catalog_without_local_keys()
    {
        var expected = SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith("provider.", StringComparison.Ordinal))
            .Select(static key => key.Value)
            .ToArray();

        RuntimeProviderActionManifest.Required
            .Select(static key => key.Value)
            .Should()
            .Equal(expected);
    }

    [Test]
    public async Task Buffered_and_streaming_turns_use_the_provider_action_terminals()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new ProviderProbe();
        var provider = new RecordingProvider(probe);
        var adapter = CreateAdapter(workspace, probe, provider);

        RuntimeProviderActionManifest.Required.Should().OnlyContain(
            key => adapter.Graph.ContainsAction(key));

        var buffered = await adapter.Kernel.RunAsync(new ChatTurnInput("buffered"));
        buffered.Completion.Content.Should().Be("provider response");

        var bufferedActions = probe.Observations
            .Select(static observation => observation.Action)
            .ToArray();
        bufferedActions.Should().ContainInOrder(
            "provider.resolve",
            "provider.client.create",
            "provider.request.prepare",
            "provider.request.serialize",
            "provider.request.serialize.after",
            "provider.request.send",
            "provider.response.deserialize",
            "provider.response.complete");
        provider.TransportCalls.Should().Be(1);
        provider.LastActionAtTransport.Should().Be("provider.request.send");

        probe.Observations.Clear();
        provider.Reset();
        var stream = new List<ChatStreamChunk>();
        await foreach (var chunk in adapter.Kernel.StreamAsync(new ChatTurnInput("stream")))
            stream.Add(chunk);

        stream.Should().ContainSingle(chunk => chunk.IsFinished);
        probe.Observations
            .Select(static observation => observation.Action)
            .Should()
            .ContainInOrder(
                "provider.stream.open",
                "provider.request.send",
                "provider.stream.close",
                "provider.response.deserialize",
                "provider.response.complete");
        provider.TransportCalls.Should().Be(1);
        provider.LastActionAtTransport.Should().Be("provider.request.send");
    }

    [Test]
    public async Task Provider_actions_preserve_root_context_and_nested_parentage()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new ProviderProbe { WaitForConcurrentTransport = true };
        var provider = new RecordingProvider(probe);
        var adapter = CreateAdapter(workspace, probe, provider);
        var firstContext = Context("provider-user-a");
        var secondContext = Context("provider-user-b");

        var first = adapter.RunRequestAsync(
            firstContext,
            new ChatTurnInput("first", Guid.NewGuid()),
            (input, ct) => adapter.Kernel.RunAsync(input, ct));
        var second = adapter.RunRequestAsync(
            secondContext,
            new ChatTurnInput("second", Guid.NewGuid()),
            (input, ct) => adapter.Kernel.RunAsync(input, ct));

        await Task.WhenAll(first.AsTask(), second.AsTask());

        probe.Observations
            .Where(static observation => observation.Action.StartsWith("provider.", StringComparison.Ordinal))
            .Should()
            .NotBeEmpty()
            .And.AllSatisfy(observation =>
            {
                new[] { "provider-user-a", "provider-user-b" }
                    .Should()
                    .Contain(observation.Subject);
                observation.Depth.Should().BeGreaterThan(0);
                observation.ParentInvocationId.Should().NotBeNull();
            });
        probe.Observations.Should().Contain(observation =>
            observation.Subject == "provider-user-a" && observation.Action == "provider.request.send");
        probe.Observations.Should().Contain(observation =>
            observation.Subject == "provider-user-b" && observation.Action == "provider.request.send");
    }

    [TestCase("replace-input")]
    [TestCase("replace-result")]
    [TestCase("repeat")]
    public async Task Provider_pure_action_controls_remain_inside_one_provider_path(string mode)
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new ProviderProbe { Mode = mode };
        var provider = new RecordingProvider(probe);
        var adapter = CreateAdapter(
            workspace,
            probe,
            provider,
            mode == "repeat" ? new MatchingRepeatEvidenceAuthority() : null);

        var result = await adapter.Kernel.RunAsync(new ChatTurnInput("controlled"));

        result.Completion.Content.Should().Be("provider response");
        provider.TransportCalls.Should().Be(1);
        if (mode == "replace-input")
            provider.LastModel.Should().Be("replaced-model");
        if (mode == "replace-result")
            probe.Observations.Count(value => value.Action == "provider.request.prepare")
                .Should().Be(1);
        if (mode == "repeat")
            probe.Observations.Count(value => value.Action == "provider.request.prepare")
                .Should().Be(2);
    }

    [TestCase("cancel")]
    [TestCase("fail")]
    public async Task Provider_action_cancellation_or_failure_stops_before_transport(string mode)
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new ProviderProbe { Mode = mode };
        var provider = new RecordingProvider(probe);
        var adapter = CreateAdapter(workspace, probe, provider);

        Func<Task> run = async () => await adapter.Kernel.RunAsync(
            new ChatTurnInput("blocked", Guid.NewGuid()));
        if (mode == "cancel")
            await run.Should().ThrowAsync<KernelActionCancelledException>();
        else
            await run.Should().ThrowAsync<KernelActionFailedException>();

        provider.TransportCalls.Should().Be(0);
        probe.Observations.Should().Contain(observation =>
            observation.Action == $"provider.request.{mode}");
    }

    private static RuntimeKernelAdapter CreateAdapter(
        TemporaryWorkspace workspace,
        ProviderProbe probe,
        RecordingProvider provider,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null)
    {
        var SourceId = "provider-boundary-test";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = provider.ProviderKey,
                ["Provider:Model"] = "original-model",
            })
            .Build();
        var options = new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
            {
                [SourceId] = RuntimeProviderActionManifest.Required.ToDictionary(
                    key => key.Value,
                    key => KernelActionCatalog.DescriptorFor(key).Capabilities,
                    StringComparer.Ordinal),
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
                        SourceId,
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

        return RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [new ProviderModule(SourceId, provider, probe)],
            workspace.Paths,
            new ProviderFactory(provider),
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
            "sharpclaw-provider-boundary-" + Guid.NewGuid().ToString("N"));

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

    private sealed class ProviderModule(
        string SourceId,
        IProviderPlugin provider,
        ProviderProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new(SourceId, "Provider boundary test", "provider-boundary");

        public void ConfigureServices(IServiceCollection module)
        {
            module.AddSingleton<IProviderPlugin>(provider);
            module.AddSingleton(probe);
            module.AddSingleton<ProviderInterceptor>();
            foreach (var action in RuntimeProviderActionManifest.Required)
            {
                module.OnAction(action).Use<ProviderInterceptor>(new HookOrdering(
                    $"provider-boundary-{action.Value}",
                    HookPriority.Normal,
                    [],
                    [],
                    TimeSpan.FromSeconds(5),
                    HookFailurePolicy.FailAction));
            }
        }

        public ValueTask StartAsync(ServiceStartContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class ProviderInterceptor(ProviderProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.Observations.Enqueue(new ProviderObservation(
                context.ActionKey.Value,
                context.Caller.SubjectId,
                context.Depth,
                context.ParentInvocationId));

            if (context.ActionKey.Value == "provider.request.prepare")
            {
                if (probe.Mode == "cancel")
                    return control.Cancel("PROVIDER_TEST_CANCELLED", "Provider action cancelled.");
                if (probe.Mode == "fail")
                    return control.Fail(new ExecutionError(
                        "PROVIDER_TEST_FAILED",
                        "Provider action failed."));
                if (probe.Mode == "repeat" && context.Attempt == 1)
                {
                    return await control.RepeatAsync(
                        new ActionRepeatRequest<KernelActionEnvelope>(
                            context.Action,
                            "Provider pure action repeat.",
                            null),
                        cancellationToken);
                }
                if (context.Action.Payload is KernelProviderRequestEnvelope request)
                {
                    if (probe.Mode == "replace-input")
                    {
                        var replacement = request with
                        {
                            Request = request.Request with
                            {
                                Profile = request.Request.Profile with
                                {
                                    ModelName = "replaced-model",
                                },
                            },
                        };
                        return await control.ProceedWithInputAsync(
                            new ActionReplacement<KernelActionEnvelope>(
                                context.Action with { Payload = replacement },
                                "Replace provider request input."),
                            cancellationToken);
                    }
                    if (probe.Mode == "replace-result")
                        return control.ReplaceResult(request, "Replace pure provider preparation result.");
                }
            }

            return await control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ProviderProbe
    {
        public ConcurrentQueue<ProviderObservation> Observations { get; } = new();
        public string? Mode { get; init; }
        public bool WaitForConcurrentTransport { get; init; }
    }

    private sealed record ProviderObservation(
        string Action,
        string Subject,
        int Depth,
        Guid? ParentInvocationId);

    private sealed class RecordingProvider(ProviderProbe probe) : IProviderPlugin, IProviderApiClient
    {
        private readonly TaskCompletionSource<bool> _bothTransports =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _transportCalls;

        public string ProviderKey => "provider-boundary";
        public string DisplayName => "Provider boundary";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public IModelCapabilityResolver Capabilities { get; } = new EmptyCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public int TransportCalls => _transportCalls;
        public string? LastModel { get; private set; }
        public string? LastActionAtTransport { get; private set; }

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["original-model", "replaced-model"]);

        public async Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken cancellationToken = default)
        {
            LastModel = model;
            LastActionAtTransport = probe.Observations.LastOrDefault()?.Action;
            var call = Interlocked.Increment(ref _transportCalls);
            if (probe.WaitForConcurrentTransport && call == 2)
                _bothTransports.TrySetResult(true);
            if (probe.WaitForConcurrentTransport)
                await _bothTransports.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return new ChatCompletionResult
            {
                Content = "provider response",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            };
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _transportCalls, 0);
            LastModel = null;
            LastActionAtTransport = null;
            while (probe.Observations.TryDequeue(out _))
            {
            }
        }
    }

    private sealed class ProviderFactory(IProviderApiClient provider) : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins) => provider;
    }

    private sealed class EmptyCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }

    private sealed class MatchingRepeatEvidenceAuthority : IKernelActionRepeatEvidenceAuthority
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
