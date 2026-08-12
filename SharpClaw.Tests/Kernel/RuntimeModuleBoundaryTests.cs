using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeModuleBoundaryTests
{
    [Test]
    public void Manifest_matches_every_published_module_action()
    {
        RuntimeModuleActionManifest.Required
            .Select(static key => key.Value)
            .Should()
            .BeEquivalentTo(
                SharpClawActionCatalog.Kernel
                    .Where(static key => key.Value.StartsWith("module.", StringComparison.Ordinal))
                    .Select(static key => key.Value));
    }

    [Test]
    public async Task Module_start_and_stop_use_the_singleton_dispatcher()
    {
        var probe = new ModuleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(probe, workspace);

        await adapter.StartAsync("test-host");
        await adapter.StopAsync();

        probe.StartCalls.Should().Be(1);
        probe.StopCalls.Should().Be(1);
        probe.Actions.Should().Contain("module.start");
        probe.Actions.Should().Contain("module.stop");
    }

    [Test]
    public async Task Replaced_commit_result_without_terminal_fails_closed()
    {
        var probe = new ModuleProbe { ReplaceResultAction = "module.enable.commit" };
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(probe, workspace);
        var terminalCalls = 0;

        Func<Task> action = async () => await adapter.RunModuleActionAsync(
            new SharpClawActionKey("module.enable.commit"),
            new RuntimeModuleActionInvocation("module", "enable"),
            (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.FromResult(true);
            });

        await action.Should()
            .ThrowAsync<KernelActionExecutionException>()
            .WithMessage("*without running its terminal*");
        terminalCalls.Should().Be(0);
        probe.Actions.Should().Contain("module.lifecycle.fail");
    }

    [Test]
    public async Task Cancelled_module_action_does_not_run_its_terminal()
    {
        var probe = new ModuleProbe { CancelAction = "module.enable.commit" };
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(probe, workspace);
        var terminalCalls = 0;

        Func<Task> action = async () => await adapter.RunModuleActionAsync(
            new SharpClawActionKey("module.enable.commit"),
            new RuntimeModuleActionInvocation("module", "enable"),
            (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.FromResult(true);
            });

        await action.Should().ThrowAsync<KernelActionCancelledException>();
        terminalCalls.Should().Be(0);
        probe.Actions.Should().Contain("module.lifecycle.cancel");
    }

    [Test]
    public async Task Failed_module_terminal_dispatches_failure_action_before_rethrow()
    {
        var probe = new ModuleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(probe, workspace);

        Func<Task> action = async () => await adapter.RunModuleActionAsync(
            new SharpClawActionKey("module.enable.commit"),
            new RuntimeModuleActionInvocation("module", "enable"),
            (_, _) => ValueTask.FromException<bool>(
                new InvalidOperationException("module test failure")));

        await action.Should().ThrowAsync<KernelActionFailedException>()
            .WithMessage("module test failure");
        probe.Actions.Should().Contain("module.lifecycle.fail");
    }

    [Test]
    public async Task Repeated_idempotent_commit_runs_the_terminal_once()
    {
        var probe = new ModuleProbe { RepeatAction = "module.enable.commit" };
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(probe, workspace, new MatchingRepeatEvidenceAuthority());
        var terminalCalls = 0;

        var result = await adapter.RunModuleActionAsync(
            new SharpClawActionKey("module.enable.commit"),
            new RuntimeModuleActionInvocation("module", "enable"),
            (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.FromResult(true);
            });

        result.Should().BeTrue();
        terminalCalls.Should().Be(1);
    }

    [Test]
    public void Production_module_lifecycle_owners_use_the_module_boundary()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? FindSourceRoot();
        var adapter = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "BLL", "Kernel", "RuntimeKernelAdapter.cs"));
        var service = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "BLL", "Services", "ModuleService.cs"));
        var health = File.ReadAllText(Path.Combine(
            root!, "SharpClaw.Runtime", "BLL", "Modules", "ModuleHealthCheckService.cs"));

        adapter.Should().Contain("DispatchModuleCompositionActions");
        adapter.Should().Contain("StartModulesThroughActionsAsync");
        adapter.Should().Contain("StopModulesThroughActionsAsync");
        service.Should().Contain("RunModulePreparationAsync");
        service.Should().Contain("RunModuleActionAsync");
        health.Should().Contain("module.health.check");
        health.Should().Contain("RunHealthActionAsync");
    }

    private static RuntimeKernelAdapter CreateAdapter(
        ModuleProbe probe,
        TemporaryWorkspace workspace,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null)
    {
        var provider = new ModuleProvider();
        var module = new ModuleActionModule(provider, probe);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "module-test",
                ["Provider:Model"] = "module-model",
            })
            .Build();
        var actionKeys = new[]
        {
            "module.start",
            "module.stop",
            "module.enable.commit",
            "module.lifecycle.fail",
            "module.lifecycle.cancel",
        };
        var grants = actionKeys.ToDictionary(
            action => action,
            action => KernelActionCatalog.DescriptorFor(new SharpClawActionKey(action)).Capabilities,
            StringComparer.Ordinal);

        return new RuntimeKernelAdapter(
            configuration,
            new ServiceCollection().BuildServiceProvider(),
            new InMemoryConversationStore(),
            [module],
            workspace.CreateInstancePaths(),
            new ModuleProviderClientFactory(provider),
            new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    [module.Identity.Id] = grants,
                },
            },
            repeatEvidenceAuthority);
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

    private sealed class ModuleProbe
    {
        public ConcurrentQueue<string> Actions { get; } = new();

        public string? CancelAction { get; init; }

        public string? ReplaceResultAction { get; init; }

        public string? RepeatAction { get; init; }

        public int StartCalls { get; set; }

        public int StopCalls { get; set; }
    }

    private sealed class ModuleActionModule(
        IProviderPlugin provider,
        ModuleProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("module-action-test", "Module action test", "module-action");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<IProviderPlugin>(provider);
            module.Services.AddSingleton(new ModuleActionInterceptor(probe));
            foreach (var actionName in new[]
            {
                "module.start",
                "module.stop",
                "module.enable.commit",
                "module.lifecycle.fail",
                "module.lifecycle.cancel",
            })
            {
                module.Hooks
                    .For(new SharpClawActionKey(actionName))
                    .Use<ModuleActionInterceptor>(new HookOrdering(
                        $"module-action-{actionName}",
                        HookPriority.Normal,
                        [],
                        [],
                        TimeSpan.FromSeconds(5),
                        HookFailurePolicy.FailAction));
            }
        }

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken cancellationToken)
        {
            probe.StartCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            probe.StopCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ModuleActionInterceptor(ModuleProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.Actions.Enqueue(context.ActionKey.Value);
            if (string.Equals(probe.CancelAction, context.ActionKey.Value, StringComparison.Ordinal))
                return control.Cancel("MODULE_TEST_CANCELLED", "Module action cancelled.");

            if (string.Equals(probe.ReplaceResultAction, context.ActionKey.Value, StringComparison.Ordinal))
                return control.ReplaceResult(true, "Module action result replacement.");

            if (string.Equals(probe.RepeatAction, context.ActionKey.Value, StringComparison.Ordinal)
                && context.Attempt == 1)
            {
                return await control.RepeatAsync(
                    new ActionRepeatRequest<KernelActionEnvelope>(
                        context.Action,
                        "Module action repeat.",
                        null),
                    cancellationToken);
            }

            return await control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ModuleProviderClientFactory(IProviderApiClient client)
        : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(
            IConfiguration configuration,
            IReadOnlyList<IProviderPlugin> plugins) => client;
    }

    private sealed class ModuleProvider : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => "module-test";
        public string DisplayName => "Module test";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public IModelCapabilityResolver Capabilities { get; } = new EmptyCapabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;
        public IProviderApiClient CreateClient(ProviderClientOptions options) => this;
        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["module-model"]);
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
                Content = "module-response",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
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

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-k11-module-" + Guid.NewGuid().ToString("N"));

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
}
