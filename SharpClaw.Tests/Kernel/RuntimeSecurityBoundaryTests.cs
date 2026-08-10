using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeSecurityBoundaryTests
{
    [Test]
    public void Security_manifest_matches_the_published_catalog_without_defining_local_actions()
    {
        var expected = SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith("security.", StringComparison.Ordinal))
            .Select(static key => key.Value)
            .ToArray();

        RuntimeSecurityActionManifest.Required
            .Select(static key => key.Value)
            .Should()
            .Equal(expected);
    }

    [Test]
    public async Task Security_actions_use_published_descriptors_and_isolate_request_contexts()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new SecurityProbe();
        var adapter = CreateAdapter(workspace, probe);
        probe.Dispatcher = adapter.ActionDispatcher;
        probe.Snapshot = adapter.Graph.ActionSnapshot;

        var requests = RuntimeSecurityActionManifest.Required
            .Select((key, index) => adapter.RunSecurityDecisionAsync(
                ExecutionContext($"security-user-{index}"),
                key,
                new RuntimeSecurityActionInvocation(key.Value, $"/security/{index}"),
                static (_, _) => ValueTask.FromResult(true)))
            .ToArray();

        (await Task.WhenAll(requests.Select(static request => request.AsTask())))
            .Should()
            .OnlyContain(static allowed => allowed);

        probe.Observations.Should().HaveCount(RuntimeSecurityActionManifest.Required.Count + 1);
        for (var index = 0; index < RuntimeSecurityActionManifest.Required.Count; index++)
        {
            var key = RuntimeSecurityActionManifest.Required[index];
            probe.Observations.Should().Contain(observation =>
                observation.Action == key.Value
                && observation.Subject == $"security-user-{index}");
        }

        probe.Observations.Should().Contain(observation =>
            observation.Action == "security.secret.read"
            && observation.Subject == "security-user-6"
            && observation.Depth > 0);
    }

    [Test]
    public async Task Api_key_middleware_allows_valid_keys_and_denies_missing_keys_through_security_action()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new SecurityProbe();
        var configuration = Configuration();
        var adapter = CreateAdapter(workspace, probe, configuration);
        var keyProvider = new ApiKeyProvider(workspace.Paths);

        var allowedNext = false;
        var allowed = HttpContext("api-user");
        allowed.Request.Path = "/protected";
        allowed.Request.Headers["X-Api-Key"] = keyProvider.ApiKey;
        var allowedMiddleware = new ApiKeyMiddleware(
            context =>
            {
                allowedNext = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            keyProvider,
            configuration,
            adapter);

        await allowedMiddleware.InvokeAsync(allowed);

        allowedNext.Should().BeTrue();
        allowed.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);

        var denied = HttpContext("api-user");
        denied.Request.Path = "/protected";
        denied.Response.Body = new MemoryStream();
        var deniedMiddleware = new ApiKeyMiddleware(
            _ => throw new AssertionException("A denied request reached the protected terminal."),
            keyProvider,
            configuration,
            adapter);

        await deniedMiddleware.InvokeAsync(denied);

        denied.Response.StatusCode.Should().Be(StatusCodes.Status423Locked);
        probe.Observations.Count(observation => observation.Action == "security.api_key.resolve")
            .Should().Be(2);
    }

    [Test]
    public async Task Api_key_repeat_does_not_repeat_the_protected_pipeline()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new SecurityProbe { RepeatAction = "security.api_key.resolve" };
        var configuration = Configuration();
        var adapter = CreateAdapter(
            workspace,
            probe,
            configuration,
            new MatchingRepeatEvidenceAuthority());
        var keyProvider = new ApiKeyProvider(workspace.Paths);
        var context = HttpContext("api-user");
        context.Request.Path = "/protected";
        context.Request.Headers["X-Api-Key"] = keyProvider.ApiKey;
        var nextCalls = 0;

        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                Interlocked.Increment(ref nextCalls);
                return Task.CompletedTask;
            },
            keyProvider,
            configuration,
            adapter);

        await middleware.InvokeAsync(context);

        nextCalls.Should().Be(1);
        probe.Observations.Count(observation => observation.Action == "security.api_key.resolve")
            .Should().Be(2);
    }

    [Test]
    public async Task Api_key_result_replacement_cannot_grant_invalid_base_authority()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new SecurityProbe
        {
            ReplaceResultAction = "security.api_key.resolve",
            ReplaceResultValue = true,
        };
        var configuration = Configuration();
        var adapter = CreateAdapter(workspace, probe, configuration);
        var keyProvider = new ApiKeyProvider(workspace.Paths);
        var context = HttpContext("api-user");
        context.Request.Path = "/protected";
        context.Response.Body = new MemoryStream();
        var nextCalls = 0;
        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                Interlocked.Increment(ref nextCalls);
                return Task.CompletedTask;
            },
            keyProvider,
            configuration,
            adapter);

        await middleware.InvokeAsync(context);

        nextCalls.Should().Be(0);
        context.Response.StatusCode.Should().Be(StatusCodes.Status423Locked);
    }

    [Test]
    public async Task Repeatable_pairing_decision_does_not_repeat_the_mutation_terminal()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new SecurityProbe { RepeatAction = "security.remote_pairing.validate" };
        var adapter = CreateAdapter(
            workspace,
            probe,
            repeatEvidenceAuthority: new MatchingRepeatEvidenceAuthority());
        var baseDecisionCalls = 0;
        var mutationCalls = 0;

        var allowed = await adapter.RunSecurityDecisionAsync(
            ExecutionContext("pairing-admin"),
            Action("security.remote_pairing.validate"),
            new RuntimeSecurityActionInvocation("approve", "/approve"),
            (_, _) =>
            {
                Interlocked.Increment(ref baseDecisionCalls);
                return ValueTask.FromResult(true);
            });
        if (allowed)
            Interlocked.Increment(ref mutationCalls);

        allowed.Should().BeTrue();
        baseDecisionCalls.Should().Be(1);
        mutationCalls.Should().Be(1);
    }

    [Test]
    public async Task Security_result_replacement_can_restrict_but_cannot_grant_the_base_decision()
    {
        using var workspace = new TemporaryWorkspace();
        var restrictingProbe = new SecurityProbe
        {
            ReplaceResultAction = "security.remote_pairing.validate",
            ReplaceResultValue = false,
        };
        var restrictingAdapter = CreateAdapter(workspace, restrictingProbe);
        var restricted = await restrictingAdapter.RunSecurityDecisionAsync(
            ExecutionContext("pairing-admin"),
            Action("security.remote_pairing.validate"),
            new RuntimeSecurityActionInvocation("revoke", "/revoke"),
            static (_, _) => ValueTask.FromResult(true));
        restricted.Should().BeFalse();

        using var secondWorkspace = new TemporaryWorkspace();
        var grantingProbe = new SecurityProbe
        {
            ReplaceResultAction = "security.remote_pairing.validate",
            ReplaceResultValue = true,
        };
        var grantingAdapter = CreateAdapter(secondWorkspace, grantingProbe);
        var granted = await grantingAdapter.RunSecurityDecisionAsync(
            ExecutionContext("pairing-admin"),
            Action("security.remote_pairing.validate"),
            new RuntimeSecurityActionInvocation("revoke", "/revoke"),
            static (_, _) => ValueTask.FromResult(false));
        granted.Should().BeFalse();
    }

    [Test]
    public async Task Session_administrator_secret_and_pairing_actions_fail_closed()
    {
        using var workspace = new TemporaryWorkspace();
        var actionKeys = new[]
        {
            "security.session.validate",
            "security.administrator.authorize",
            "security.secret.read",
            "security.secret.write",
            "security.secret.delete",
            "security.remote_pairing.validate",
        };

        foreach (var actionName in actionKeys)
        {
            var probe = new SecurityProbe { FailureAction = actionName };
            var adapter = CreateAdapter(workspace, probe);
            var terminalCalled = false;

            Func<Task> action = async () => await adapter.RunSecurityDecisionAsync(
                ExecutionContext($"denied-{actionName}"),
                Action(actionName),
                new RuntimeSecurityActionInvocation("authorize", "/security"),
                (_, _) =>
                {
                    terminalCalled = true;
                    return ValueTask.FromResult(true);
                });

            await action.Should().ThrowAsync<Exception>();
            terminalCalled.Should().BeFalse();
            probe.Observations.Should().ContainSingle(observation =>
                observation.Action == actionName
                && observation.Subject == $"denied-{actionName}");
        }
    }

    [Test]
    public async Task Security_action_cancellation_does_not_run_the_terminal()
    {
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(workspace, new SecurityProbe());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var terminalCalled = false;

        Func<Task> action = async () => await adapter.RunSecurityDecisionAsync(
            ExecutionContext("cancelled-user"),
            Action("security.remote_pairing.validate"),
            new RuntimeSecurityActionInvocation("validate", "/pairings"),
            (_, _) =>
            {
                terminalCalled = true;
                return ValueTask.FromResult(true);
            },
            cancellation.Token);

        await action.Should().ThrowAsync<Exception>();
        terminalCalled.Should().BeFalse();
    }

    [Test]
    public async Task Security_action_failure_is_returned_without_running_the_terminal()
    {
        using var workspace = new TemporaryWorkspace();
        var probe = new SecurityProbe { FailureAction = "security.secret.delete" };
        var adapter = CreateAdapter(workspace, probe);
        var terminalCalled = false;

        Func<Task> action = async () => await adapter.RunSecurityDecisionAsync(
            ExecutionContext("failed-user"),
            Action("security.secret.delete"),
            new RuntimeSecurityActionInvocation("delete", "/env/core"),
            (_, _) =>
            {
                terminalCalled = true;
                return ValueTask.FromResult(true);
            });

        await action.Should().ThrowAsync<Exception>();
        terminalCalled.Should().BeFalse();
    }

    [Test]
    public void Live_security_terminals_use_the_dispatcher_and_excluded_legacy_terminals_stay_out_of_the_host()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? FindSourceRoot();

        var hostProject = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "Host", "SharpClaw.Runtime.Host.csproj"));
        var bllProject = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "BLL", "SharpClaw.Runtime.BLL.csproj"));
        var apiKeySource = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "Host", "Api", "ApiKeyMiddleware.cs"));
        var endpointSource = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "Host", "KernelHostEndpoints.cs"));
        var pairingSource = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "Host", "Handlers", "RemoteRuntimePairingHandlers.cs"));

        apiKeySource.Should().Contain("RunSecurityDecisionAsync");
        apiKeySource.Should().Contain("await next(context)");
        endpointSource.Should().Contain("security.secret.read");
        pairingSource.Should().Contain("RunSecurityDecisionAsync");
        pairingSource.Should().NotContain("RunSecurityActionAsync");
        pairingSource.Should().NotContain("return Task.FromResult<IResult>(");
        hostProject.Should().Contain("Compile Remove=\"Api\\JwtSessionMiddleware.cs\"");
        hostProject.Should().Contain("Compile Remove=\"Handlers\\**\\*.cs\"");
        hostProject.Should().Contain("Compile Include=\"Handlers\\RemoteRuntimePairingHandlers.cs\"");
        hostProject.Should().Contain("Compile Remove=\"Cli\\**\\*.cs\"");
        bllProject.Should().Contain("Compile Remove=\"**\\*.cs\"");
        bllProject.Should().Contain("Compile Include=\"Kernel\\**\\*.cs\"");
    }

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "SharpClaw.Runtime")))
                return directory.FullName;
        }

        throw new AssertionException("The SharpClaw source root could not be located.");
    }

    private static RuntimeKernelAdapter CreateAdapter(
        TemporaryWorkspace workspace,
        SecurityProbe probe,
        IConfiguration? configuration = null,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null)
    {
        var provider = new SecurityProvider();
        var moduleId = "security-boundary-test";
        var grants = RuntimeSecurityActionManifest.Required.ToDictionary(
            key => key.Value,
            key => KernelActionCatalog.DescriptorFor(key).Capabilities,
            StringComparer.Ordinal);
        return new RuntimeKernelAdapter(
            configuration ?? Configuration(),
            new ServiceCollection().BuildServiceProvider(),
            new InMemoryConversationStore(),
            [new SecurityModule(provider, probe)],
            workspace.Paths,
            new SecurityProviderFactory(provider),
            new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    [moduleId] = grants,
                },
                SensitiveActionApprovals = RuntimeSecurityActionManifest.Required
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
            },
            repeatEvidenceAuthority);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provider:Key"] = "security-test",
            ["Provider:Model"] = "security-test-model",
        })
        .Build();

    private static KernelActionExecutionContext ExecutionContext(string subject) =>
        new(
            new RequestPrincipal(subject, subject, new HashSet<string>(StringComparer.Ordinal), true),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

    private static DefaultHttpContext HttpContext(string subject)
    {
        var context = new DefaultHttpContext();
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier,
                    subject)],
                "K03Test"));
        return context;
    }

    private static SharpClawActionKey Action(string value) => new(value);

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-security-" + Guid.NewGuid().ToString("N"));

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

    private sealed class SecurityProbe
    {
        public ConcurrentQueue<SecurityObservation> Observations { get; } = new();

        public IActionDispatcher? Dispatcher { get; set; }

        public ActionPipelineSnapshot? Snapshot { get; set; }

        public string? FailureAction { get; init; }

        public string? RepeatAction { get; init; }

        public string? ReplaceResultAction { get; init; }

        public bool ReplaceResultValue { get; init; }

        public int NestedDispatches;
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

    private sealed record SecurityObservation(string Action, string Subject, int Depth);

    private sealed class SecurityInterceptor(SecurityProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.Observations.Enqueue(new SecurityObservation(
                context.ActionKey.Value,
                context.Caller.SubjectId,
                context.Depth));

            if (context.ActionKey.Value == "security.remote_pairing.validate"
                && Interlocked.Exchange(ref probe.NestedDispatches, 1) == 0
                && probe.Dispatcher is { } dispatcher
                && probe.Snapshot is { } snapshot)
            {
                var nestedKey = new SharpClawActionKey("security.secret.read");
                var nestedDescriptor = KernelActionCatalog.DescriptorFor(nestedKey).ToDescriptor();
                await dispatcher.RunRequiredAsync<KernelActionEnvelope, object>(
                    nestedDescriptor,
                    new KernelActionEnvelope(
                        nestedKey,
                        new RuntimeSecurityActionInvocation("nested-read", "/env/core")),
                    static (_, _) => ValueTask.FromResult<object>(true),
                    snapshot,
                    cancellationToken);
            }

            if (string.Equals(
                    probe.FailureAction,
                    context.ActionKey.Value,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("K03 test action failure.");
            }

            if (string.Equals(
                    probe.RepeatAction,
                    context.ActionKey.Value,
                    StringComparison.Ordinal)
                && context.Attempt == 1)
            {
                return await control.RepeatAsync(
                    new ActionRepeatRequest<KernelActionEnvelope>(
                        context.Action,
                        "K03 repeat boundary test",
                        null),
                    cancellationToken);
            }

            if (string.Equals(
                    probe.ReplaceResultAction,
                    context.ActionKey.Value,
                    StringComparison.Ordinal))
            {
                return control.ReplaceResult(
                    probe.ReplaceResultValue,
                    "K03 result boundary test");
            }

            return await control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class SecurityModule(
        IProviderPlugin provider,
        SecurityProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("security-boundary-test", "Security boundary test", "security");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<IProviderPlugin>(provider);
            module.Services.AddSingleton(probe);
            module.Services.AddSingleton<SecurityInterceptor>();
            foreach (var action in RuntimeSecurityActionManifest.Required)
            {
                module.Hooks.For(action).Use<SecurityInterceptor>(new HookOrdering(
                    $"security-boundary-{action.Value}",
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

    private sealed class SecurityProviderFactory(IProviderApiClient provider)
        : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins) => provider;
    }

    private sealed class SecurityProvider : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => "security-test";
        public string DisplayName => "Security test";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public IModelCapabilityResolver Capabilities { get; } = new EmptyCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;

        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["security-test-model"]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatCompletionResult
            {
                Content = "security-test-response",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
    }

    private sealed class EmptyCapabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }
}
