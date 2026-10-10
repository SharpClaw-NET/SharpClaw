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

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class RuntimeKernelAdapterTests
{
    [Test]
    public async Task ProviderModelCatalogUsesTheRegisteredProviderWithoutInferenceAsync()
    {
        var provider = new RecordingProviderClient();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        { ["Provider:Key"] = "test", ["Provider:Model"] = "test-model" }).Build();
        using var workspace = new TemporaryWorkspace();
        var factory = new RecordingProviderClientFactory(provider);
        var adapter = RuntimeKernelAdapterTestFactory.Create(configuration, [new ProviderModule(provider)],
            workspace.CreateInstancePaths(), factory);
        var models = await RuntimeProviderModelCatalog.ReadAsync(new(RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty, Guid.NewGuid(), Guid.NewGuid()), configuration, adapter, factory, default).ConfigureAwait(false);
        models.ProviderKey.Should().Be("test");
        models.Models.Should().Equal("test-model");
        provider.Messages.Should().BeEmpty();
        provider.SystemPrompts.Should().BeEmpty();
    }

    [Test]
    public async Task Adapter_compiles_registration_graph_and_routes_direct_chat_through_registration_providerAsync()
    {
        var provider = new RecordingProviderClient();
        var module = new ProviderModule(provider);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();
        using var workspace = new TemporaryWorkspace();
        var instancePaths = workspace.CreateInstancePaths();
        var providerFactory = new RecordingProviderClientFactory(provider);

        var adapter = RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [module],
            instancePaths,
            providerFactory);

        adapter.Graph.GetService(typeof(IEnumerable<IProviderPlugin>))
            .Should().NotBeNull();
        providerFactory.Plugins.Should().BeNull("no provider client is created before a chat turn");

        await adapter.StartAsync("test-host").ConfigureAwait(false);
        var result = await adapter.Kernel.RunAsync(new ChatTurnInput("hello")).ConfigureAwait(false);
        await adapter.StopAsync().ConfigureAwait(false);

        result.Completion.Content.Should().Be("reply");
        providerFactory.Plugins.Should().ContainSingle()
            .Which.ProviderKey.Should().Be("test");
        provider.Messages.Should().ContainSingle(message => message.Content == "hello");
        module.Started.Should().BeTrue();
        module.Stopped.Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public async Task UnconfiguredAdapterStartsAndServesNonChatActionsWithoutCreatingAClientAsync(string? key)
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Provider:Key"] = key }).Build();
        var factory = new RecordingProviderClientFactory(new RecordingProviderClient());
        var adapter = RuntimeKernelAdapterTestFactory.Create(configuration, [], workspace.CreateInstancePaths(), factory);
        await adapter.StartAsync("unconfigured-host").ConfigureAwait(false);
        try
        {
            var result = await adapter.RunRequestAsync(
                adapter.CreateCliExecutionContext(RequestPrincipal.Anonymous), "setup",
                static (value, _) => ValueTask.FromResult(value)).ConfigureAwait(false);
            result.Should().Be("setup");
            factory.Plugins.Should().BeNull();
        }
        finally { await adapter.StopAsync().ConfigureAwait(false); }
    }

    [Test]
    public async Task CredentialConfigurationIsNotRequiredToConstructOrStartTheHostGraphAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var provider = new RecordingProviderClient(requiresApiKey: true);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Provider:Key"] = "test" }).Build();
        var adapter = RuntimeKernelAdapterTestFactory.Create(configuration, [new ProviderModule(provider)],
            workspace.CreateInstancePaths(), new RuntimeProviderClientFactory());
        await adapter.StartAsync("missing-credentials-host").ConfigureAwait(false);
        await adapter.StopAsync().ConfigureAwait(false);
        provider.Messages.Should().BeEmpty();
        RuntimeProviderSetup.Describe(configuration, adapter).SetupRequired.Should().BeTrue();
    }

    [Test]
    public void ExplicitlyUnknownProviderStillFailsGraphConstruction()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Provider:Key"] = "not-installed" }).Build();
        Action construct = () => RuntimeKernelAdapterTestFactory.Create(configuration,
            [new ProviderModule(new RecordingProviderClient())], workspace.CreateInstancePaths(),
            new RuntimeProviderClientFactory());
        construct.Should().Throw<InvalidOperationException>().WithMessage("*not available*");
    }

    [TestCase(true), TestCase(false)]
    public async Task Module_profile_routes_each_turn_and_module_prompt_reaches_each_provider_onceAsync(bool hostDefaultConfigured)
    {
        var primary = new RecordingProviderClient();
        var alternate = new RecordingProviderClient("alternate");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Provider:Key"] = hostDefaultConfigured ? "test" : null,
                ["Provider:Model"] = hostDefaultConfigured ? "test-model" : null,
            })
            .Build();
        using var workspace = new TemporaryWorkspace();
        var adapter = RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [new ProviderModule(primary), new ProviderModule(alternate)],
            workspace.CreateInstancePaths(),
            new RuntimeProviderClientFactory(),
            configureServices: services =>
            {
                services.AddSingleton<IChatProfileResolver, SwitchingProfileResolver>();
                services.AddSingleton<IChatContextContributor, ModulePromptContributor>();
            });

        RuntimeProviderSetup.Describe(configuration, adapter).SetupRequired.Should().BeFalse();

        await adapter.Kernel.RunAsync(new ChatTurnInput("primary")).ConfigureAwait(false);
        await adapter.Kernel.RunAsync(new ChatTurnInput("alternate")).ConfigureAwait(false);
        var streamed = new List<ChatStreamChunk>();
        await foreach (var chunk in adapter.Kernel.StreamAsync(new ChatTurnInput("alternate")).ConfigureAwait(false))
            streamed.Add(chunk);

        primary.SystemPrompts.Should().Equal("profile instructions\n\nmodule instructions");
        alternate.SystemPrompts.Should().Equal(
            "profile instructions\n\nmodule instructions",
            "profile instructions\n\nmodule instructions");
        primary.Messages.Should().NotContain(message => message.Role == "system");
        alternate.Messages.Should().NotContain(message => message.Role == "system");
        primary.Messages.Should().ContainSingle(message => message.Content == "primary");
        alternate.Messages.Should().Contain(message => message.Content == "alternate");
        streamed.Should().ContainSingle(chunk => chunk.IsFinished);
    }

    [Test]
    public async Task Adapter_dispatches_request_ingress_through_the_compiled_action_graphAsync()
    {
        var provider = new RecordingProviderClient();
        var module = new ProviderModule(provider);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();
        using var workspace = new TemporaryWorkspace();
        var adapter = RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [module],
            workspace.CreateInstancePaths(),
            new RecordingProviderClientFactory(provider));

        adapter.Graph.ContainsAction(new SharpClawActionKey("runtime.request.receive"))
            .Should().BeTrue();
        var executionContext = new KernelActionExecutionContext(
            new RequestPrincipal(
                "request-user",
                "Request user",
                new HashSet<string>(StringComparer.Ordinal),
                true),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());
        var result = await adapter.RunRequestAsync(
            executionContext,
            "request-payload",
            static (payload, _) => ValueTask.FromResult(payload.Length)).ConfigureAwait(false);

        result.Should().Be("request-payload".Length);
    }

    [TestCase(false), TestCase(true)]
    [Parallelizable]
    [CancelAfter(90_000)]
    public async Task RequestHandlerOutlivesIngressDeadlineWithoutChangingKernelBudgetsAsync(bool streaming)
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder().Build();
        var adapter = RuntimeKernelAdapterTestFactory.Create(configuration, [], workspace.CreateInstancePaths(),
            new RecordingProviderClientFactory(new RecordingProviderClient()));
        adapter.Graph.GetStandardAction(new SharpClawActionKey("runtime.request.receive"))
            .DefaultTimeout.Should().Be(TimeSpan.FromSeconds(30));
        adapter.Graph.GetStandardAction(new SharpClawActionKey("runtime.request.handler.invoke"))
            .DefaultTimeout.Should().Be(TimeSpan.FromMinutes(2));
        var context = new KernelActionExecutionContext(RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty, Guid.NewGuid(), Guid.NewGuid());
        var calls = 0;
        var completed = false;

        async ValueTask<string> Buffered(string value, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(TimeSpan.FromSeconds(32), cancellationToken).ConfigureAwait(false);
            completed = true;
            return value;
        }

        async IAsyncEnumerable<string> Stream(string value,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            yield return "before";
            await Task.Delay(TimeSpan.FromSeconds(32), cancellationToken).ConfigureAwait(false);
            completed = true;
            yield return value;
        }

        if (streaming)
        {
            var chunks = new List<string>();
            await foreach (var chunk in adapter.RunRequestStreamAsync(context, "after", Stream,
                TestContext.CurrentContext.CancellationToken).ConfigureAwait(false))
            {
                if (chunks.Count == 0)
                    completed.Should().BeFalse("the first chunk must not wait for handler completion");
                chunks.Add(chunk);
            }
            chunks.Should().Equal("before", "after");
        }
        else
        {
            (await adapter.RunRequestAsync(context, "after", Buffered,
                TestContext.CurrentContext.CancellationToken).ConfigureAwait(false))
                .Should().Be("after");
        }

        completed.Should().BeTrue();
        calls.Should().Be(1);
    }

    [TestCase(false, "inspect"), TestCase(true, "inspect")]
    [TestCase(false, "replace"), TestCase(true, "replace")]
    [TestCase(false, "cancel-receive"), TestCase(true, "cancel-receive")]
    [TestCase(false, "cancel-handler"), TestCase(true, "cancel-handler")]
    [TestCase(false, "replace-handler-result"), TestCase(true, "replace-handler-result")]
    public async Task RequestStagesPreserveModuleControlsAndCallerAuthorityAsync(bool streaming, string mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        using var workspace = new TemporaryWorkspace();
        var probe = new RequestStageProbe(mode);
        var module = new RequestStageRegistration(probe);
        var adapter = RuntimeKernelAdapterTestFactory.Create(new ConfigurationBuilder().Build(), [module],
            workspace.CreateInstancePaths(), new RecordingProviderClientFactory(new RecordingProviderClient()),
            RequestStageApprovals(module.Identity.Id));
        var features = new ExtensionFeatureSet([
            new ExtensionFeature("test.request", 1, "request-stage-probe", 128,
                JsonSerializer.SerializeToElement("request-feature"))]);
        var context = new KernelActionExecutionContext(new RequestPrincipal("request-caller", "Request caller",
            new HashSet<string>(StringComparer.Ordinal) { "operator" }, true),
            features, Guid.NewGuid(), Guid.NewGuid());
        var calls = 0;
        var chunks = new List<string>();
        var expected = string.Equals(mode, "replace", StringComparison.Ordinal) ? "rewritten-request" : "request";
        async IAsyncEnumerable<string> Stream(string value,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            await Task.Yield();
            yield return value;
        }
        async Task Consume()
        {
            if (streaming)
            {
                await foreach (var item in adapter.RunRequestStreamAsync(context, "request", Stream).ConfigureAwait(false))
                    chunks.Add(item);
            }
            else
            {
                var result = await adapter.RunRequestAsync(context, "request", (value, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref calls);
                    return ValueTask.FromResult(value);
                }).ConfigureAwait(false);
                chunks.Add(result);
            }
        }
        Func<Task> consume = Consume;
        if (mode.StartsWith("cancel-", StringComparison.Ordinal))
        {
            await consume.Should().ThrowAsync<KernelActionCancelledException>().ConfigureAwait(false);
            calls.Should().Be(0);
            chunks.Should().BeEmpty();
        }
        else if (string.Equals(mode, "replace-handler-result", StringComparison.Ordinal))
        {
            await consume.Should().ThrowAsync<KernelActionExecutionException>()
                .WithMessage("*without running its terminal*").ConfigureAwait(false);
            calls.Should().Be(0);
            chunks.Should().BeEmpty();
        }
        else
        {
            await consume().ConfigureAwait(false);
            calls.Should().Be(1);
            chunks.Should().Equal(expected);
        }

        probe.Contexts.Select(item => item.ActionKey.Value).Should().Equal(string.Equals(mode, "cancel-receive", StringComparison.Ordinal) ? ["runtime.request.receive"] : ["runtime.request.receive", "runtime.request.handler.invoke"]);
        foreach (var observation in probe.Contexts)
        {
            observation.Caller.SubjectId.Should().Be(context.Caller.SubjectId);
            observation.Caller.IsAuthenticated.Should().BeTrue();
            observation.Caller.Roles.Should().ContainSingle().Which.Should().Be("operator");
            observation.TraceId.Should().Be(context.TraceId);
            observation.IdempotencyKey.Should().Be(context.IdempotencyKey);
            observation.Features.Items.Should().ContainSingle().Which.Value.GetString().Should().Be("request-feature");
        }
    }

    [TestCase(false), TestCase(true)]
    public async Task RequestCancellationBeforeIngressDoesNotInvokeHandlerAsync(bool streaming)
    {
        using var workspace = new TemporaryWorkspace();
        var adapter = RuntimeKernelAdapterTestFactory.Create(new ConfigurationBuilder().Build(), [],
            workspace.CreateInstancePaths(), new RecordingProviderClientFactory(new RecordingProviderClient()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var context = new KernelActionExecutionContext(RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty, Guid.NewGuid(), Guid.NewGuid());
        var calls = 0;
        async IAsyncEnumerable<string> Stream(string value,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            await Task.Yield();
            yield return value;
        }
        Func<Task> run = async () =>
        {
            if (streaming)
            {
                await foreach (var unused in adapter.RunRequestStreamAsync(context, "request", Stream,
                    cancellation.Token).ConfigureAwait(false))
                    Assert.Fail("A canceled ingress must not emit a chunk: " + unused);
            }
            else
            {
                await adapter.RunRequestAsync(context, "request", (value, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return ValueTask.FromResult(value);
                }, cancellation.Token).ConfigureAwait(false);
            }
        };

        await run.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
        calls.Should().Be(0);
    }

    private static KernelGraphCompileOptions RequestStageApprovals(string moduleId)
    {
        var grants = new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal);
        var approvals = new List<KernelSensitiveActionApproval>();
        foreach (var name in new[] { "runtime.request.receive", "runtime.request.handler.invoke" })
        {
            var key = new SharpClawActionKey(name);
            var descriptor = KernelActionCatalog.DescriptorFor(key).ToDescriptor();
            var types = KernelSchemaIdentity.ActionTypes(descriptor, typeof(KernelActionEnvelope), typeof(object));
            grants.Add(name, descriptor.Capabilities);
            approvals.Add(new KernelSensitiveActionApproval(moduleId, key, descriptor.Version,
                types.ActionType.AssemblyQualifiedName!, types.ResultType.AssemblyQualifiedName!,
                KernelSchemaIdentity.Action(descriptor)));
        }
        return new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
            { [moduleId] = grants },
            SensitiveActionApprovals = approvals,
        };
    }

    [Test]
    public async Task Adapter_compiles_and_runs_the_complete_published_jobs_catalogAsync()
    {
        var provider = new RecordingProviderClient();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();
        using var workspace = new TemporaryWorkspace();
        var adapter = RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [new ProviderModule(provider)],
            workspace.CreateInstancePaths(),
            new RecordingProviderClientFactory(provider));

        SharpClawActionCatalog.Jobs.Should().HaveCount(138);
        adapter.Graph.ActionSnapshot.ActionGrants.Select(grant => grant.ActionKey).Should()
            .BeEquivalentTo(KernelActionCatalog.Descriptors.Select(descriptor => descriptor.Key)
                .Append(RuntimeStartupActionDefinitions.Initialize.Key));
        adapter.Graph.GetActionDescriptor<string, bool>(RuntimeStartupActionDefinitions.Initialize.Key)
            .Should().Be(RuntimeStartupActionDefinitions.Initialize);
        SharpClawActionCatalog.Jobs.Should().OnlyContain(key => adapter.Graph.ContainsAction(key));

        var terminalCalls = 0;
        var context = new KernelActionExecutionContext(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());
        var result = await adapter.JobsActionRunner.RunAsync<KernelJobsOperationFamilies.Read>(
            new SharpClawActionKey("jobs.read"),
            CreateJob("jobs.read"),
            (job, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(job with
                {
                    Result = new JobResultReference("test", 1, "read"),
                });
            },
            context).ConfigureAwait(false);

        result.Result!.ArtifactKey.Should().Be("read");
        terminalCalls.Should().Be(1);
    }

    [Test]
    public async Task Jobs_boundary_rejects_cancellation_before_the_terminalAsync()
    {
        var provider = new RecordingProviderClient();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();
        using var workspace = new TemporaryWorkspace();
        var adapter = RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [new ProviderModule(provider)],
            workspace.CreateInstancePaths(),
            new RecordingProviderClientFactory(provider));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var terminalCalls = 0;

        Func<Task> run = async () =>
            await adapter.JobsActionRunner.RunAsync<KernelJobsOperationFamilies.Read>(
                new SharpClawActionKey("jobs.read"),
                CreateJob("jobs.read"),
                static (job, _) => ValueTask.FromResult(job),
                new KernelActionExecutionContext(
                    RequestPrincipal.Anonymous,
                    ExtensionFeatureSet.Empty,
                    Guid.NewGuid(),
                    Guid.NewGuid()),
                0,
                cancellation.Token).ConfigureAwait(false);

        await run.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
        terminalCalls.Should().Be(0);
    }

    private static JobDocument CreateJob(string action) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            new SharpClawActionKey(action),
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            JobStatus.Pending,
            [],
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            ActionOutcomeCertainty.Certain,
            new JobPayloadEnvelope("test", 1, "{}"));

    [Test]
    public async Task Request_stream_replace_result_without_terminal_fails_closedAsync()
    {
        var provider = new RecordingProviderClient();
        var module = new StreamReplacementRegistration(provider);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();
        using var workspace = new TemporaryWorkspace();
        var conversationStore = new InMemoryConversationStore();
        var actionKey = new SharpClawActionKey("runtime.request.receive");
        var descriptor = KernelActionCatalog.DescriptorFor(actionKey).ToDescriptor();
        var types = KernelSchemaIdentity.ActionTypes(
            descriptor,
            typeof(KernelActionEnvelope),
            typeof(object));
        var adapter = RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [module],
            workspace.CreateInstancePaths(),
            new RecordingProviderClientFactory(provider),
            new KernelGraphCompileOptions
            {
                ActionRegistrationCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
                {
                    [module.Identity.Id] = new Dictionary<
                        string,
                        ActionInterceptionCapabilities>(StringComparer.Ordinal)
                    {
                        [actionKey.Value] = descriptor.Capabilities,
                    },
                },
                SensitiveActionApprovals =
                [
                    new KernelSensitiveActionApproval(
                        module.Identity.Id,
                        actionKey,
                        descriptor.Version,
                        types.ActionType.AssemblyQualifiedName!,
                        types.ResultType.AssemblyQualifiedName!,
                        KernelSchemaIdentity.Action(
                            descriptor,
                            typeof(KernelActionEnvelope),
                            typeof(object))),
                ],
            });
        var conversationId = Guid.NewGuid();
        var executionContext = new KernelActionExecutionContext(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());
        var chunks = new List<ChatStreamChunk>();

        Func<Task> consume = async () =>
        {
            await foreach (var chunk in adapter.RunRequestStreamAsync(
                               executionContext,
                               "stream-request",
                               (_, ct) => adapter.Kernel.StreamAsync(
                                   new ChatTurnInput("must-not-run", conversationId),
                                   ct)).ConfigureAwait(false))
            {
                chunks.Add(chunk);
            }
        };

        await consume.Should()
            .ThrowAsync<KernelActionExecutionException>()
            .WithMessage("*without running its terminal*").ConfigureAwait(false);

        chunks.Should().BeEmpty();
        provider.Messages.Should().BeEmpty();
        (await conversationStore.LoadHistoryAsync(
            conversationId,
            TestOperationContext(),
            CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
    }

    [Test]
    public async Task Adapter_uses_stateless_chat_without_context_registrationAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Provider:Key"] = "test",
                ["Provider:Model"] = "test-model",
            })
            .Build();
        using var workspace = new TemporaryWorkspace();
        var instancePaths = workspace.CreateInstancePaths();
        var firstStore = new InMemoryConversationStore();
        var firstRegistration = new ProviderModule(new RecordingProviderClient());
        var firstAdapter = RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [firstRegistration],
            instancePaths,
            new RecordingProviderClientFactory(firstRegistration.Provider));

        await firstAdapter.StartAsync("test-host").ConfigureAwait(false);
        var firstResult = await firstAdapter.Kernel.RunAsync(new ChatTurnInput("first")).ConfigureAwait(false);
        await firstAdapter.StopAsync().ConfigureAwait(false);

        var secondRegistration = new ProviderModule(new RecordingProviderClient());
        var secondAdapter = RuntimeKernelAdapterTestFactory.Create(
            configuration,
            [secondRegistration],
            new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                instancePaths.InstanceRoot,
                instancePaths.SharedRoot,
                instancePaths.InstallAnchor),
            new RecordingProviderClientFactory(secondRegistration.Provider));

        await secondAdapter.StartAsync("test-host").ConfigureAwait(false);
        var secondResult = await secondAdapter.Kernel.RunAsync(new ChatTurnInput("second")).ConfigureAwait(false);
        await secondAdapter.StopAsync().ConfigureAwait(false);

        secondResult.ConversationId.Should().NotBe(firstResult.ConversationId);
        (await firstStore.LoadHistoryAsync(
            firstResult.ConversationId,
            TestOperationContext(),
            CancellationToken.None).ConfigureAwait(false))
            .Should().BeEmpty();
        (await firstStore.LoadHistoryAsync(
            secondResult.ConversationId,
            TestOperationContext(),
            CancellationToken.None).ConfigureAwait(false))
            .Should().BeEmpty();
    }

    private static ChatOperationContext TestOperationContext() =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty);

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-kernel-" + Guid.NewGuid().ToString("N"));

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

    private sealed class ProviderModule(IProviderPlugin provider) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new($"test-module-{provider.ProviderKey}", "Test module", "test");

        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public IProviderApiClient Provider => (IProviderApiClient)provider;

        public void ConfigureServices(IServiceCollection module) =>
            module.AddSingleton<IProviderPlugin>(provider);

        public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct)
        {
            Started = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken ct)
        {
            Stopped = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RequestStageRegistration(RequestStageProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("request-stage-probe", "Request stage probe", "requests");

        public void ConfigureServices(IServiceCollection module)
        {
            module.AddSingleton(probe);
            foreach (var name in new[] { "runtime.request.receive", "runtime.request.handler.invoke" })
                module.OnAction(new SharpClawActionKey(name)).Use<RequestStageProbe>(
                    new HookOrdering("request-stage-probe", Timeout: TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class RequestStageProbe(string mode) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public List<ActionContext<KernelActionEnvelope>> Contexts { get; } = [];

        public ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            var ingress = string.Equals(context.ActionKey.Value, "runtime.request.receive", StringComparison.Ordinal);
            if ((string.Equals(mode, "cancel-receive", StringComparison.Ordinal) && ingress) || (string.Equals(mode, "cancel-handler", StringComparison.Ordinal) && !ingress))
                return ValueTask.FromResult(control.Cancel("REQUEST_TEST_CANCELLED", "Request stage test cancellation."));
            if (string.Equals(mode, "replace-handler-result", StringComparison.Ordinal) && !ingress)
                return ValueTask.FromResult(control.ReplaceResult("must-not-accept", "Request stage test replacement."));
            if (string.Equals(mode, "replace", StringComparison.Ordinal) && ingress)
                return control.ProceedWithInputAsync(new ActionReplacement<KernelActionEnvelope>(
                    context.Action with { Payload = "rewritten-request" }, "Request stage test input replacement."),
                    cancellationToken);
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class StreamReplacementRegistration(IProviderPlugin provider) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("stream-replacement-module", "Stream replacement module", "stream-replace");

        public void ConfigureServices(IServiceCollection module)
        {
            module.AddSingleton<IProviderPlugin>(provider);
            module.AddSingleton<StreamReplacementInterceptor>();
            module.OnAction(new SharpClawActionKey("runtime.request.receive"))
                .Use<StreamReplacementInterceptor>(new HookOrdering(
                    "stream-replacement-test",
                    HookPriority.Normal,
                    [],
                    [],
                    TimeSpan.FromSeconds(5),
                    HookFailurePolicy.FailAction));
        }

        public ValueTask StartAsync(ServiceStartContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class StreamReplacementInterceptor
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(control.ReplaceResult(
                true,
                "K06 stream replacement test"));
    }

    private sealed class RecordingProviderClientFactory(IProviderApiClient client)
        : IRuntimeProviderClientFactory
    {
        public IReadOnlyList<IProviderPlugin>? Plugins { get; private set; }

        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins,
            string providerKey)
        {
            Plugins = plugins;
            return client;
        }
    }

    private sealed class RecordingProviderClient(string providerKey = "test", bool requiresApiKey = false) : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => providerKey;
        public string DisplayName => "Test";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => requiresApiKey;
        public IModelCapabilityResolver Capabilities { get; } =
            new EmptyCapabilityResolver();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public List<ChatCompletionMessage> Messages { get; } = [];
        public List<string?> SystemPrompts { get; } = [];

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default)
        {
            SystemPrompts.Add(systemPrompt);
            Messages.AddRange(messages);
            return Task.FromResult(new ChatCompletionResult
            {
                Content = "reply",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
        }
    }

    private sealed class SwitchingProfileResolver : IChatProfileResolver
    {
        public ValueTask<ChatProfile> ResolveAsync(
            ChatTurnContext turn,
            ChatOperationContext context,
            CancellationToken ct) =>
            ValueTask.FromResult(new ChatProfile(
                string.Equals(turn.Input.Message, "alternate", StringComparison.Ordinal) ? "alternate" : "test",
                Guid.Empty,
                "test-model",
                "profile instructions"));
    }

    private sealed class ModulePromptContributor : IChatContextContributor
    {
        public ValueTask<ChatContextContribution> ContributeAsync(
            ChatContextRequest request,
            ChatOperationContext context,
            CancellationToken ct) =>
            ValueTask.FromResult(new ChatContextContribution(
                [new SystemPromptSegment("module", "module instructions")],
                [],
                []));
    }

    private sealed class EmptyCapabilityResolver : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }
}
