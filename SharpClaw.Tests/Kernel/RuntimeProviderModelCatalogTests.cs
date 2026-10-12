using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "NUnit constructs this regression fixture.")]
[TestFixture]
internal sealed class RuntimeProviderModelCatalogTests
{
    private static readonly string[] Stages =
    ["provider.resolve", "provider.client.create", "provider.request.prepare", "provider.request.send", "provider.response.deserialize"];
    private static readonly string[] ExpectedModels = ["model"];

    [TestCase(false), TestCase(true)]
    public async Task RepeatedMetadataAndChatShareOneSubscriptionAndRetainEveryActionAsync(bool concurrent)
    {
        using var workspace = new Workspace(concurrent);
        var work = Enumerable.Range(0, 8).Select(_ => workspace.ReadAsync()).ToArray();
        Exception?[] outcomes;
        try
        {
            if (concurrent)
            {
                await workspace.Probe.AllListed.Task.WaitAsync(TimeSpan.FromSeconds(5),
                    TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
                work.Should().OnlyContain(task => !task.IsCompleted);
                workspace.Primary.Creations.Should().Be(1);
                workspace.Probe.Subscriptions.Should().Be(1);
            }
        }
        finally
        {
            workspace.Probe.Release.TrySetResult();
            outcomes = await TestTaskOutcome.JoinAsync(work).ConfigureAwait(false);
        }
        outcomes.Should().OnlyContain(exception => exception == null);
        var catalogs = await Task.WhenAll(work).ConfigureAwait(false);
        catalogs.Should().OnlyContain(catalog => catalog.ProviderKey == "primary" && catalog.Models.SequenceEqual(ExpectedModels));
        foreach (var stage in Stages)
            workspace.Probe.Actions.Count(action => string.Equals(action.Key, stage, StringComparison.Ordinal)).Should().Be(8);
        workspace.Probe.Actions.Should().OnlyContain(action => action.Subject == "metadata-user");
        workspace.Primary.Creations.Should().Be(1);
        workspace.Primary.LastCredential.Should().Be("primary-sentinel");
        workspace.Probe.ChatCalls.Should().Be(0);
        workspace.Probe.Unload();
        workspace.Probe.UnloadCalls.Should().Be(1);
        (await workspace.Adapter.Kernel.RunAsync(new ChatTurnInput("fixture chat"),
            TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Completion.Content.Should().Be("fixture reply");
        workspace.Primary.Creations.Should().Be(1);
        workspace.Probe.ChatCalls.Should().Be(1);
        workspace.Probe.Subscriptions.Should().Be(1);
    }

    [TestCase("cancel"), TestCase("rewrite")]
    public async Task AReusedClientCannotBypassCurrentProviderActionAuthorityAsync(string mode)
    {
        using var workspace = new Workspace(false);
        await workspace.ReadAsync().ConfigureAwait(false);
        workspace.Probe.Mode = mode;
        var error = await TestTaskOutcome.CaptureAsync(workspace.ReadAsync()).ConfigureAwait(false);
        if (mode.Equals("cancel", StringComparison.Ordinal)) error.Should().BeOfType<KernelActionCancelledException>();
        else error.Should().BeOfType<KernelActionFailedException>();
        workspace.Primary.Creations.Should().Be(1);
        workspace.Probe.ListCalls.Should().Be(1);
        workspace.Probe.Subscriptions.Should().Be(1);
        workspace.Probe.ChatCalls.Should().Be(0);
    }

    [Test]
    public async Task MissingCredentialsCannotCreateAMetadataClientAsync()
    {
        using var workspace = new Workspace(false, credential: null);
        var failure = await TestTaskOutcome.CaptureAsync(workspace.ReadAsync()).ConfigureAwait(false);
        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(RuntimeProviderSetup.RequiredErrorMessage);
        workspace.Primary.Creations.Should().Be(0);
        workspace.Probe.Subscriptions.Should().Be(0);
        workspace.Probe.ListCalls.Should().Be(0);
    }

    [Test]
    public async Task MetadataCannotBorrowForeignOrDefaultCredentialsForAnotherProviderAsync()
    {
        using var workspace = new Workspace(false);
        await workspace.ReadAsync().ConfigureAwait(false);
        var foreign = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        { ["Provider:Key"] = "alternate", ["Provider:Model"] = "model", ["Provider:ApiKey"] = "foreign-sentinel" }).Build();
        var failure = await TestTaskOutcome.CaptureAsync(workspace.ReadAsync(foreign)).ConfigureAwait(false);
        failure.Should().BeOfType<KernelActionFailedException>().Which.Error.Code.Should().Be("ACTION_TERMINAL_FAILED");
        workspace.Alternate.Creations.Should().Be(0);
        workspace.Primary.LastCredential.Should().Be("primary-sentinel");
        workspace.Probe.Subscriptions.Should().Be(1);
        workspace.Probe.ListCalls.Should().Be(1);
    }

    [Test]
    public async Task FailedCreationRemainsOwnedWithoutRepeatedSubscriptionsAsync()
    {
        using var workspace = new Workspace(false);
        workspace.Primary.FailCreation = true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var failure = await TestTaskOutcome.CaptureAsync(workspace.ReadAsync()).ConfigureAwait(false);
            failure.Should().BeOfType<KernelActionFailedException>().Which.Error.Code.Should().Be("ACTION_TERMINAL_FAILED");
        }
        workspace.Primary.Creations.Should().Be(1);
        workspace.Probe.Subscriptions.Should().Be(0);
        workspace.Probe.ListCalls.Should().Be(0);
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "sharpclaw-model-catalog-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _services;
        private readonly IConfigurationRoot _configuration;
        public Probe Probe { get; }
        public Provider Primary { get; }
        public Provider Alternate { get; }
        public RuntimeKernelAdapter Adapter { get; }

        public Workspace(bool concurrent, string? credential = "primary-sentinel")
        {
            Probe = new(concurrent);
            Primary = new("primary", Probe); Alternate = new("alternate", Probe);
            _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            { ["Provider:Key"] = "primary", ["Provider:Model"] = "model", ["Provider:ApiKey"] = credential }).Build();
            var registration = new Registration(Probe, Primary, Alternate);
            _services = TestServiceGraph.Collect([registration]).BuildServiceProvider();
            try
            {
                var options = CompileOptions();
                Adapter = new(_configuration, _services, new KernelJobsBindings(), [],
                    new(SharpClawInstanceKind.Backend, _directory, _directory, _directory), new RuntimeProviderClientFactory(), options);
            }
            catch { _services.Dispose(); throw; }
        }

        public Task<SharpClawProviderModels> ReadAsync(IConfiguration? configuration = null) =>
            RuntimeProviderModelCatalog.ReadAsync(new(new RequestPrincipal("metadata-user", "metadata-user",
                new HashSet<string>(StringComparer.Ordinal), true), ExtensionFeatureSet.Empty, Guid.NewGuid(), Guid.NewGuid()),
                configuration ?? _configuration, Adapter, TestContext.CurrentContext.CancellationToken).AsTask();

        public void Dispose()
        {
            Probe.Release.TrySetResult();
            _services.Dispose();
            Probe.RetireSubscriptions();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private static KernelGraphCompileOptions CompileOptions() => new()
    {
        ActionRegistrationCapabilityGrants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
        {
            ["catalog-test"] = Stages.ToDictionary(name => name,
                name => KernelActionCatalog.DescriptorFor(new SharpClawActionKey(name)).Capabilities, StringComparer.Ordinal),
        },
        SensitiveActionApprovals = Stages.Select(name =>
        {
            var key = new SharpClawActionKey(name);
            var descriptor = KernelActionCatalog.DescriptorFor(key).ToDescriptor();
            var types = KernelSchemaIdentity.ActionTypes(descriptor, typeof(KernelActionEnvelope), typeof(object));
            return new KernelSensitiveActionApproval("catalog-test", key, descriptor.Version,
                types.ActionType.AssemblyQualifiedName!, types.ResultType.AssemblyQualifiedName!, KernelSchemaIdentity.Action(descriptor));
        }).ToArray(),
    };

    private sealed class Registration(Probe probe, Provider primary, Provider alternate) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("catalog-test", "Catalog test", "catalog-test");
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IProviderPlugin>(primary); services.AddSingleton<IProviderPlugin>(alternate);
            services.AddSingleton(probe); services.AddSingleton<Interceptor>();
            foreach (var stage in Stages)
                services.OnAction(new SharpClawActionKey(stage)).Use<Interceptor>(new HookOrdering("catalog-" + stage));
        }
    }

    private sealed class Interceptor(Probe probe) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken)
        {
            probe.Actions.Enqueue((context.ActionKey.Value, context.Caller.SubjectId));
            if (context.ActionKey.Value.Equals("provider.client.create", StringComparison.Ordinal))
            {
                if (probe.Mode.Equals("cancel", StringComparison.Ordinal))
                    return ValueTask.FromResult(control.Cancel("CATALOG_DENIED", "Controlled metadata creation rejection."));
                if (probe.Mode.Equals("rewrite", StringComparison.Ordinal))
                    return control.ProceedWithInputAsync(new ActionReplacement<KernelActionEnvelope>(
                        context.Action with { Payload = "foreign provider authority" }, "Controlled metadata rewrite."), cancellationToken);
            }
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class Probe(bool concurrent)
    {
        private Action? _unloaded;
        private int _listCalls;
        private int _unloadCalls;
        private int _chatCalls;
        public ConcurrentQueue<(string Key, string? Subject)> Actions { get; } = new();
        public TaskCompletionSource AllListed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Mode { get; set; } = "observe";
        public int ListCalls => Volatile.Read(ref _listCalls);
        public int UnloadCalls => Volatile.Read(ref _unloadCalls);
        public int ChatCalls => Volatile.Read(ref _chatCalls);
        public int Subscriptions => _unloaded?.GetInvocationList().Length ?? 0;
        public void Subscribe() => _unloaded += () => Interlocked.Increment(ref _unloadCalls);
        public void Unload() => _unloaded?.Invoke();
        public void RetireSubscriptions() => _unloaded = null;
        public async Task<IReadOnlyList<string>> ListAsync()
        {
            if (Interlocked.Increment(ref _listCalls) == 8) AllListed.TrySetResult();
            if (concurrent) await Release.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            return ["model"];
        }
        public ChatCompletionResult Chat() { Interlocked.Increment(ref _chatCalls); return new() { Content = "fixture reply" }; }
    }

    private sealed class Provider(string key, Probe probe) : IProviderPlugin, IProviderCredentialBoundPlugin
    {
        private int _creations;
        public string ProviderKey => key;
        public string DisplayName => key;
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => true;
        public IModelCapabilityResolver Capabilities { get; } = new EmptyCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public string? LastCredential { get; private set; }
        public bool FailCreation { get; set; }
        public int Creations => Volatile.Read(ref _creations);
        public IProviderApiClient CreateClient(ProviderClientOptions options) => throw new InvalidOperationException("Credential binding required.");
        public IProviderApiClient CreateClient(ProviderClientOptions options, string credential)
        {
            Interlocked.Increment(ref _creations);
            if (FailCreation) throw new IOException("Controlled provider creation fault.");
            LastCredential = credential;
            return new Client(key, probe);
        }
        public IProviderCostFeed? CreateCostFeed(ProviderClientOptions options, string credential) => null;
    }

    private sealed class Client : IProviderApiClient
    {
        private readonly Probe _probe;
        public string ProviderKey { get; }
        public Client(string key, Probe probe) { ProviderKey = key; _probe = probe; probe.Subscribe(); }
        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) => _probe.ListAsync();
        public Task<ChatCompletionResult> ChatCompletionAsync(string model, string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages, int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null, CompletionParameters? completionParameters = null,
            CancellationToken ct = default) => Task.FromResult(_probe.Chat());
    }

    private sealed class EmptyCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }
}
