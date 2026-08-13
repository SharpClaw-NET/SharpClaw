using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeEventBoundaryTests
{
    [Test]
    public void Manifest_contains_every_published_event_action()
    {
        RuntimeEventActionManifest.Required
            .Select(static key => key.Value)
            .Should()
            .BeEquivalentTo(
                SharpClawActionCatalog.Kernel
                    .Where(static key => key.Value.StartsWith("event.", StringComparison.Ordinal))
                    .Select(static key => key.Value));
    }

    [Test]
    public async Task Inline_publish_runs_the_declared_event_actions_once()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new EventProbe();
        var adapter = CreateAdapter(workspace, probe);

        var result = await adapter.PublishAsync(
            new RuntimeEventPayload("turn.completed", "test", "A turn completed."));

        result.EventId.Should().NotBe(Guid.Empty);
        result.Payload.Name.Should().Be("turn.completed");
        result.Delivery.Should().Be(EventDelivery.Inline);
        foreach (var action in new[]
                 {
                     "event.define",
                     "event.publish.preview",
                     "event.publish.commit",
                     "event.deliver",
                 })
        {
            probe.Actions.Count(value => value == action).Should().Be(1, action);
        }
    }

    [Test]
    public async Task Define_replacement_flows_to_the_committed_event()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new EventProbe
        {
            ReplacementPayload = new RuntimeEventPayload(
                "replaced.event",
                "replacement",
                "The replacement payload is committed."),
        };
        var adapter = CreateAdapter(workspace, probe);

        var result = await adapter.PublishAsync(
            new RuntimeEventPayload("original.event", "original", "The original payload."));

        result.Payload.Should().Be(probe.ReplacementPayload);
    }

    [Test]
    public async Task Durable_publish_enqueues_one_runtime_event_after_delivery()
    {
        using var workspace = new TemporaryWorkspace();
        var sink = new RecordingSink();
        var adapter = CreateAdapter(workspace, new EventProbe(), sink);

        var result = await adapter.PublishAsync(
            new RuntimeEventPayload("durable.event", "test", "A durable event."),
            EventDelivery.Durable);

        result.Delivery.Should().Be(EventDelivery.Durable);
        sink.Events.Should().ContainSingle(item =>
            item.EventKey == new SharpClawEventKey("runtime.event") &&
            item.Delivery == EventDelivery.Durable);
    }

    [Test]
    public async Task Replace_result_without_terminal_fails_closed()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new EventProbe
        {
            ReplaceResultAction = "event.publish.commit",
        };
        var sink = new RecordingSink();
        var adapter = CreateAdapter(workspace, probe, sink);

        Func<Task> publish = async () => await adapter.PublishAsync(
            new RuntimeEventPayload("blocked.event", "test", "This event must not publish."));

        await publish.Should()
            .ThrowAsync<KernelActionExecutionException>()
            .WithMessage("*without running its terminal*");
        sink.Events.Should().BeEmpty();
        probe.Actions.Should().Contain("event.delivery.fail");
    }

    [Test]
    public async Task Cancellation_dispatches_failure_without_delivery()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new EventProbe
        {
            CancelAction = "event.publish.commit",
        };
        var sink = new RecordingSink();
        var adapter = CreateAdapter(workspace, probe, sink);

        Func<Task> publish = async () => await adapter.PublishAsync(
            new RuntimeEventPayload("cancelled.event", "test", "This event is cancelled."));

        await publish.Should().ThrowAsync<KernelActionCancelledException>();
        sink.Events.Should().BeEmpty();
        probe.Actions.Should().Contain("event.delivery.fail");
    }

    [Test]
    public async Task Delivery_sink_routes_enqueue_mutation_through_the_event_action()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new EventProbe();
        var adapter = CreateAdapter(workspace, probe);
        var store = new RecordingOutboxStore();
        var sink = new RuntimeEventDeliverySink(new FixedScopeFactory(adapter, store));
        var eventId = Guid.NewGuid();
        var envelope = new EventEnvelope<RuntimeEventPayload>(
            eventId,
            null,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "sharpclaw.runtime.events",
            new RuntimeEventPayload("queued.event", "test", "A queued event."));

        await sink.EnqueueAsync(
            new SharpClawEventKey("runtime.event"),
            envelope,
            EventDelivery.Durable,
            CancellationToken.None,
            "test-listener");

        probe.Actions.Should().ContainSingle(value => value == "event.enqueue");
        store.Messages.Should().ContainSingle(message =>
            message.EventId == eventId && message.TargetListenerId == "test-listener");
    }

    [Test]
    public async Task Outbox_state_transitions_run_through_the_declared_event_actions_once()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new EventProbe();
        var adapter = CreateAdapter(workspace, probe);
        var store = new RecordingOutboxStore();
        var service = new RuntimeEventOutboxService(
            new FixedBoundaryAccessor(adapter),
            store);
        var now = DateTimeOffset.UtcNow;
        var record = new RuntimeEventOutboxRecord(
            "event-record",
            Guid.NewGuid(),
            new SharpClawEventKey("runtime.event"),
            "{}",
            EventDelivery.Durable,
            "test-listener",
            "pending",
            0,
            null,
            now,
            now);

        await service.AcknowledgeAsync(record);
        await service.FailAsync(record, "temporary delivery failure");
        await service.CancelAsync(record);

        probe.Actions.Count(value => value == "event.acknowledge").Should().Be(1);
        probe.Actions.Count(value => value == "event.delivery.fail").Should().Be(2);
        store.Acknowledged.Should().ContainSingle().Which.Should().Be(record.RecordKey);
        store.Failures.Should().ContainSingle(item =>
            item.RecordKey == record.RecordKey && item.Error == "temporary delivery failure");
        store.Cancelled.Should().ContainSingle().Which.Should().Be(record.RecordKey);
    }

    [Test]
    public void Production_event_delivery_owners_use_the_kernel_event_boundary()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? FindSourceRoot();
        var adapter = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "BLL", "Kernel", "RuntimeKernelAdapter.cs"));
        var sink = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "BLL", "Kernel", "RuntimeEventDeliverySink.cs"));
        var service = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "Host", "RuntimeEventOutboxService.cs"));
        var store = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "Host", "RuntimeModuleStorageEventOutboxStore.cs"));
        var bllProject = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "BLL", "SharpClaw.Runtime.BLL.csproj"));
        var kernelSources = Directory.EnumerateFiles(
                Path.Combine(root!, "SharpClaw.Runtime", "BLL", "Kernel"),
                "*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .ToArray();

        adapter.Should().Contain("RunEventActionAsync");
        adapter.Should().Contain("_eventDispatcher.PublishAsync");
        sink.Should().Contain("RunEventActionAsync");
        sink.Should().Contain("store.EnqueueAsync");
        service.Should().Contain("RunEventActionAsync");
        store.Should().Contain("SaveChangesThroughKernelAsync");
        bllProject.Should().Contain("Compile Include=\"Kernel\\**\\*.cs\"");
        kernelSources.Should().NotContain(source => source.Contains(
            "ModuleEventDispatcher",
            StringComparison.Ordinal));
    }

    private static RuntimeKernelAdapter CreateAdapter(
        TemporaryWorkspace workspace,
        EventProbe probe,
        IKernelEventDeliverySink? sink = null)
    {
        var provider = new EventProvider();
        var module = new EventModule(provider, probe);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "event-test",
                ["Provider:Model"] = "event-model",
            })
            .Build();
        var actionGrants = RuntimeEventActionManifest.Required.ToDictionary(
            key => key.Value,
            key => KernelActionCatalog.DescriptorFor(key).Capabilities,
            StringComparer.Ordinal);
        var options = new KernelGraphCompileOptions
        {
            ActionModuleCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
            {
                [module.Identity.Id] = actionGrants,
            },
        };

        return new RuntimeKernelAdapter(
            configuration,
            new ServiceCollection().BuildServiceProvider(),
            [module],
            workspace.CreateInstancePaths(),
            new EventProviderClientFactory(provider),
            options,
            eventDeliverySink: sink ?? new InMemoryEventDeliverySink(supportsDurable: true));
    }

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

    private sealed class EventProbe
    {
        public ConcurrentQueue<string> Actions { get; } = new();

        public string? CancelAction { get; init; }

        public string? ReplaceResultAction { get; init; }

        public RuntimeEventPayload? ReplacementPayload { get; init; }
    }

    private sealed class EventModule(
        IProviderPlugin provider,
        EventProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("event-test-module", "Event test module", "event-test");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<IProviderPlugin>(provider);
            module.Services.AddSingleton(probe);
            module.Services.AddSingleton<EventActionInterceptor>();
            foreach (var action in RuntimeEventActionManifest.Required)
            {
                module.Hooks
                    .For(action)
                    .Use<EventActionInterceptor>(new HookOrdering(
                        $"event-test-{action.Value}",
                        HookPriority.Normal,
                        [],
                        [],
                        TimeSpan.FromSeconds(5),
                        HookFailurePolicy.FailAction));
            }
        }

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class EventActionInterceptor(EventProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.Actions.Enqueue(context.ActionKey.Value);
            if (string.Equals(probe.CancelAction, context.ActionKey.Value, StringComparison.Ordinal))
                return control.Cancel("EVENT_TEST_CANCELLED", "Event action cancelled.");

            if (string.Equals(probe.ReplaceResultAction, context.ActionKey.Value, StringComparison.Ordinal))
                return control.ReplaceResult(true, "Event result replacement.");

            if (probe.ReplacementPayload is not null &&
                context.ActionKey.Value == "event.define" &&
                context.Action.Payload is RuntimeEventActionInvocation invocation)
            {
                var replacement = context.Action with
                {
                    Payload = invocation with { Payload = probe.ReplacementPayload },
                };
                return await control.ProceedWithInputAsync(
                    new ActionReplacement<KernelActionEnvelope>(replacement, "Event input replacement."),
                    cancellationToken);
            }

            return await control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class RecordingSink : IKernelEventDeliverySink
    {
        public List<KernelQueuedEvent> Events { get; } = [];

        public bool SupportsDurable => true;

        public ValueTask EnqueueAsync(
            SharpClawEventKey eventKey,
            object envelope,
            EventDelivery delivery,
            CancellationToken cancellationToken) =>
            EnqueueAsync(eventKey, envelope, delivery, cancellationToken, "unknown");

        public ValueTask EnqueueAsync(
            SharpClawEventKey eventKey,
            object envelope,
            EventDelivery delivery,
            CancellationToken cancellationToken,
            string targetListenerId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(new KernelQueuedEvent(
                eventKey,
                envelope,
                delivery,
                DateTimeOffset.UtcNow,
                targetListenerId));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOutboxStore : IRuntimeEventOutboxStore
    {
        public List<RuntimeEventOutboxMessage> Messages { get; } = [];

        public List<string> Acknowledged { get; } = [];

        public List<(string RecordKey, string Error)> Failures { get; } = [];

        public List<string> Cancelled { get; } = [];

        public ValueTask EnqueueAsync(
            RuntimeEventOutboxMessage message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<RuntimeEventOutboxRecord>> ReadPendingAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<RuntimeEventOutboxRecord>>([]);

        public ValueTask AcknowledgeAsync(
            string recordKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acknowledged.Add(recordKey);
            return ValueTask.CompletedTask;
        }

        public ValueTask FailAsync(
            string recordKey,
            string error,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Failures.Add((recordKey, error));
            return ValueTask.CompletedTask;
        }

        public ValueTask CancelAsync(
            string recordKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Cancelled.Add(recordKey);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedBoundaryAccessor(IRuntimeEventActionBoundary boundary)
        : IRuntimeEventActionBoundaryAccessor
    {
        public IRuntimeEventActionBoundary GetRequiredBoundary() => boundary;
    }

    private sealed class FixedScopeFactory(
        IRuntimeEventActionBoundary boundary,
        IRuntimeEventOutboxStore store) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(new FixedServiceProvider(boundary, store));
    }

    private sealed class FixedScope(IServiceProvider serviceProvider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public void Dispose()
        {
        }
    }

    private sealed class FixedServiceProvider(
        IRuntimeEventActionBoundary boundary,
        IRuntimeEventOutboxStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IRuntimeEventActionBoundary)
                ? boundary
                : serviceType == typeof(IRuntimeEventOutboxStore)
                    ? store
                    : null;
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-event-" + Guid.NewGuid().ToString("N"));

        public SharpClawInstancePaths CreateInstancePaths()
        {
            Directory.CreateDirectory(_root);
            var paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                _root,
                _root,
                _root);
            paths.EnsureDirectories();
            return paths;
        }

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

    private sealed class EventProviderClientFactory(IProviderApiClient client)
        : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins) => client;
    }

    private sealed class EventProvider : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => "event-test";
        public string DisplayName => "Event test provider";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public IModelCapabilityResolver Capabilities { get; } = new EmptyCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["event-model"]);

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
                Content = "event-reply",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
    }

    private sealed class EmptyCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }
}
