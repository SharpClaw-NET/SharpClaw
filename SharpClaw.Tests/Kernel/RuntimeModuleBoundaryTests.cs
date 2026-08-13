using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Services;
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
    public void Production_composition_captures_declared_module_actions_and_events()
    {
        var probe = new ModuleProbe();
        var declared = new DeclaredContractModule();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(
            probe,
            workspace,
            additionalModules: [declared]);

        var contract = adapter.ModuleContracts
            .Single(value => value.ModuleId == declared.Identity.Id);

        contract.Actions.Should().ContainSingle(value =>
            value.Key == DeclaredContractModule.ActionKey &&
            value.Version == 1 &&
            value.ActionType == typeof(KernelActionEnvelope) &&
            value.ResultType == typeof(object));
        contract.Events.Should().ContainSingle(value =>
            value.Key == DeclaredContractModule.EventKey &&
            value.Version == 1 &&
            value.EventType == typeof(DeclaredContractEvent));
        adapter.Graph.ContainsAction(DeclaredContractModule.ActionKey).Should().BeTrue();
        adapter.Graph.ContainsEvent(DeclaredContractModule.EventKey).Should().BeTrue();
        adapter.ModuleContracts
            .Select(value => value.ModuleId)
            .Should()
            .Contain(RuntimeJobsActionModule.ModuleId)
            .And.Contain(RuntimeEventDefinitions.ModuleId);
    }

    [Test]
    public async Task Declared_module_action_uses_the_compiled_wildcard_hook()
    {
        var probe = new ModuleProbe();
        var declared = new DeclaredContractModule();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(
            probe,
            workspace,
            additionalModules: [declared]);
        var descriptor = adapter.Graph.GetStandardAction(DeclaredContractModule.ActionKey);

        var result = await adapter.ActionDispatcher.RunRequiredAsync(
            descriptor,
            new KernelActionEnvelope(DeclaredContractModule.ActionKey, "payload"),
            static (action, _) => ValueTask.FromResult(action.Payload!),
            adapter.Graph.ActionSnapshot,
            CancellationToken.None);

        result.Should().Be("payload");
        declared.WildcardCalls.Should().BeGreaterThan(1);
        declared.WildcardKeys.Should().Contain(DeclaredContractModule.ActionKey.Value);
    }

    [Test]
    public async Task Integrated_wildcard_executes_every_roadmap_action_with_exact_set_equality()
    {
        var probe = new ModuleProbe();
        var declared = new DeclaredContractModule(actionWildcardEnabled: false);
        var clientProbe = new ClientCoverageProbe();
        var gatewayProbe = new GatewayCoverageProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(
            probe,
            workspace,
            additionalModules: [declared],
            includeTool: true);

        declared.ResetWildcardObservation();

        var observedCoverageKeys = await RunRoadmapCoverageOwnersAsync(
            adapter,
            declared,
            clientProbe,
            gatewayProbe);

        var jobsBefore = declared.WildcardKeys.Count;
        await RunAllJobsFamiliesAsync(adapter);
        var jobsObserved = declared.WildcardKeys
            .Skip(jobsBefore)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in RuntimeJobsActionManifest.Required)
        {
            jobsObserved.Should().Contain(key.Value);
            observedCoverageKeys.Add(key.Value);
        }

        var declaredDescriptor = adapter.Graph.GetStandardAction(DeclaredContractModule.ActionKey);
        await adapter.ActionDispatcher.RunRequiredAsync(
            declaredDescriptor,
            new KernelActionEnvelope(DeclaredContractModule.ActionKey, "m01-test"),
            static (action, _) => ValueTask.FromResult(action.Payload ?? new object()),
            adapter.Graph.ActionSnapshot,
            CancellationToken.None);
        declared.WildcardKeys.Should().Contain(DeclaredContractModule.ActionKey.Value);
        observedCoverageKeys.Add(DeclaredContractModule.ActionKey.Value);

        var expectedActionKeys = KernelActionCatalog.Coverage
            .Select(static entry => entry.ActionKey.Value)
            .Concat(RuntimeJobsActionManifest.Required.Select(static key => key.Value))
            .Append(DeclaredContractModule.ActionKey.Value)
            .ToHashSet(StringComparer.Ordinal);

        observedCoverageKeys.Should().BeEquivalentTo(expectedActionKeys);
        declared.WildcardCalls.Should().BeGreaterThan(1);
        adapter.ModuleContracts
            .Single(value => value.ModuleId == declared.Identity.Id)
            .Events
            .Should()
            .ContainSingle(value => value.Key == DeclaredContractModule.EventKey);
    }

    private static async Task<HashSet<string>> RunRoadmapCoverageOwnersAsync(
        RuntimeKernelAdapter adapter,
        DeclaredContractModule declared,
        ClientCoverageProbe clientProbe,
        GatewayCoverageProbe gatewayProbe)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        await ObserveRuntimeOwnerAsync(
            declared,
            "runtime.start.prepare",
            () => adapter.RunRuntimeLifecycleActionAsync(
                new SharpClawActionKey("runtime.start.prepare"),
                "roadmap-test",
                static _ => ValueTask.CompletedTask).AsTask(),
            observed);

        await ObserveRuntimeOwnerAsync(
            declared,
            "runtime.request.receive",
            () => adapter.RunRequestAsync<object, bool>(
                CreateRoadmapExecutionContext(),
                new object(),
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(true);
                }).AsTask(),
            observed);

        await ObserveRuntimeOwnerAsync(
            declared,
            "security.session.validate",
            () => adapter.RunSecurityDecisionAsync(
                CreateRoadmapExecutionContext(),
                new SharpClawActionKey("security.session.validate"),
                new RuntimeSecurityActionInvocation("validate", "session"),
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(true);
                }).AsTask(),
            observed);

        await ObserveRuntimeOwnerAsync(
            declared,
            "runtime.cli.parse",
            () => adapter.RunCliActionAsync(
                adapter.CreateCliExecutionContext(),
                new SharpClawActionKey("runtime.cli.parse"),
                new RuntimeCliActionInvocation("parse", "help", 0),
                static _ => ValueTask.FromResult(true)).AsTask(),
            observed);

        var clientContext = new ClientActionContextSource();
        var clientDispatcher = ClientActionDispatcher.CreateProduction(
            clientContext,
            clientProbe);
        await ObserveClientOwnerAsync(
            clientProbe,
            "client.command.dispatch",
            () => clientDispatcher.RunCommandAsync(
                "roadmap.coverage",
                static _ => ValueTask.FromResult(true)).AsTask(),
            observed);

        await ObserveRuntimeOwnersAsync(
            declared,
            [
                "chat.turn.start",
                "chat.provider_round.start",
                "tool.call.propose",
            ],
            () => adapter.Kernel.RunAsync(new ChatTurnInput("roadmap coverage")).AsTask(),
            observed);

        var persistenceBoundary = (IRuntimePersistenceActionBoundary)adapter;
        await ObserveRuntimeOwnerAsync(
            declared,
            "storage.get",
            () => persistenceBoundary.RunPersistenceActionAsync(
                new RuntimePersistenceActionInvocation(
                    new SharpClawActionKey("storage.get"),
                    0,
                    0,
                    0),
                static cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(0);
                }).AsTask(),
            observed);

        var transactionBoundary = (IRuntimeTransactionActionBoundary)adapter;
        await ObserveRuntimeOwnerAsync(
            declared,
            "storage.transaction.commit",
            () => transactionBoundary.RunTransactionActionAsync(
                new RuntimeTransactionActionInvocation(
                    new SharpClawActionKey("storage.transaction.commit"),
                    null,
                    true),
                static cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(RuntimeTransactionActionResult.Completed);
                }).AsTask(),
            observed);

        await ObserveRuntimeOwnerAsync(
            declared,
            "module.start",
            () => adapter.RunModuleActionAsync(
                new SharpClawActionKey("module.start"),
                new RuntimeModuleActionInvocation("roadmap", "start"),
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(true);
                }).AsTask(),
            observed);

        await ObserveRuntimeOwnerAsync(
            declared,
            "event.deliver",
            () => adapter.RunEventActionAsync(
                new SharpClawActionKey("event.deliver"),
                new RuntimeEventActionInvocation(
                    new SharpClawEventKey("runtime.event"),
                    Guid.NewGuid(),
                    EventDelivery.Inline,
                    "deliver",
                    new RuntimeEventPayload("roadmap.coverage", "test", "Roadmap coverage.")),
                static (invocation, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(invocation);
                }).AsTask(),
            observed);

        var gatewayBoundary = CreateGatewayCoverageBoundary(gatewayProbe);
        await ObserveGatewayOwnerAsync(
            gatewayProbe,
            "background.tick.execute",
            () => gatewayBoundary.ExecuteTickAsync(
                new GatewayBackgroundTickInvocation(
                    "roadmap-coverage",
                    "execute",
                    Guid.NewGuid()),
                static _ => ValueTask.CompletedTask,
                CancellationToken.None).AsTask(),
            observed);

        var gateway = new GatewayActionMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            gatewayBoundary,
            NullLogger<GatewayActionMiddleware>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/roadmap/coverage";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "roadmap-user")],
            "test"));
        await ObserveGatewayOwnerAsync(
            gatewayProbe,
            "gateway.request.receive",
            () => gateway.InvokeAsync(httpContext),
            observed);

        return observed;
    }

    private static async Task ObserveRuntimeOwnerAsync(
        DeclaredContractModule declared,
        string expectedAction,
        Func<Task> owner,
        ISet<string> observed)
    {
        var before = declared.WildcardKeys.Count;
        await owner();
        var ownerKeys = declared.WildcardKeys.Skip(before).ToHashSet(StringComparer.Ordinal);
        ownerKeys.Should().Contain(expectedAction);
        observed.Add(expectedAction);
    }

    private static async Task ObserveRuntimeOwnersAsync(
        DeclaredContractModule declared,
        IReadOnlyList<string> expectedActions,
        Func<Task> owner,
        ISet<string> observed)
    {
        var before = declared.WildcardKeys.Count;
        await owner();
        var ownerKeys = declared.WildcardKeys.Skip(before).ToHashSet(StringComparer.Ordinal);
        foreach (var expectedAction in expectedActions)
        {
            ownerKeys.Should().Contain(expectedAction);
            observed.Add(expectedAction);
        }
    }

    private static async Task ObserveClientOwnerAsync(
        ClientCoverageProbe probe,
        string expectedAction,
        Func<Task> owner,
        ISet<string> observed)
    {
        var before = probe.ActionKeys.Count;
        await owner();
        probe.ActionKeys.Skip(before).Should().Contain(expectedAction);
        observed.Add(expectedAction);
    }

    private static async Task ObserveGatewayOwnerAsync(
        GatewayCoverageProbe probe,
        string expectedAction,
        Func<Task> owner,
        ISet<string> observed)
    {
        var before = probe.ActionKeys.Count;
        await owner();
        probe.ActionKeys.Skip(before).Should().Contain(expectedAction);
        observed.Add(expectedAction);
    }

    private static KernelActionExecutionContext CreateRoadmapExecutionContext() =>
        new(
            new RequestPrincipal(
                "roadmap-user",
                "Roadmap user",
                new HashSet<string>(StringComparer.Ordinal),
                true),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

    private static async Task RunAllJobsFamiliesAsync(RuntimeKernelAdapter adapter)
    {
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.SubmitFamily>(
            new SharpClawActionKey("jobs.submit"), "jobs.submit", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ValidateFamily>(
            new SharpClawActionKey("jobs.validate"), "jobs.validate", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.IdentityCreateFamily>(
            new SharpClawActionKey("jobs.identity.create"), "jobs.identity.create", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.QueuePersistFamily>(
            new SharpClawActionKey("jobs.queue.persist"), "jobs.queue.persist", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.HoldEvaluateFamily>(
            new SharpClawActionKey("jobs.hold.evaluate"), "jobs.hold.evaluate", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.HoldResolveFamily>(
            new SharpClawActionKey("jobs.hold.resolve"), "jobs.hold.resolve", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.DispatchFamily>(
            new SharpClawActionKey("jobs.dispatch"), "jobs.dispatch", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.StartFamily>(
            new SharpClawActionKey("jobs.start"), "jobs.start", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.HandlerInvokeFamily>(
            new SharpClawActionKey("jobs.handler.invoke"), "jobs.handler.invoke", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ProgressFamily>(
            new SharpClawActionKey("jobs.progress.report"), "jobs.progress.report", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ArtifactSealFamily>(
            new SharpClawActionKey("jobs.artifact.seal"), "jobs.artifact.seal", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.CompleteFamily>(
            new SharpClawActionKey("jobs.complete"), "jobs.complete", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.FailFamily>(
            new SharpClawActionKey("jobs.fail"), "jobs.fail", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.CancelFamily>(
            new SharpClawActionKey("jobs.cancel"), "jobs.cancel", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.CancelRequestFamily>(
            new SharpClawActionKey("jobs.cancel.request"), "jobs.cancel.request", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.CancelApplyFamily>(
            new SharpClawActionKey("jobs.cancel.apply"), "jobs.cancel.apply", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.PauseFamily>(
            new SharpClawActionKey("jobs.pause"), "jobs.pause", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.StopFamily>(
            new SharpClawActionKey("jobs.stop"), "jobs.stop", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.RecoveryFamily>(
            new SharpClawActionKey("jobs.recovery"), "jobs.recovery", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.RecoveryScanFamily>(
            new SharpClawActionKey("jobs.recovery.scan"), "jobs.recovery.scan", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.RecoveryClassifyFamily>(
            new SharpClawActionKey("jobs.recovery.classify"), "jobs.recovery.classify", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.RetryFamily>(
            new SharpClawActionKey("jobs.retry"), "jobs.retry", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.RetryEvaluateFamily>(
            new SharpClawActionKey("jobs.retry.evaluate"), "jobs.retry.evaluate", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.RetryScheduleFamily>(
            new SharpClawActionKey("jobs.retry.schedule"), "jobs.retry.schedule", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ResumeFamily>(
            new SharpClawActionKey("jobs.resume"), "jobs.resume", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.DeleteFamily>(
            new SharpClawActionKey("jobs.delete"), "jobs.delete", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ReadFamily>(
            new SharpClawActionKey("jobs.read"), "jobs.read", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ListFamily>(
            new SharpClawActionKey("jobs.list"), "jobs.list", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.LogsReadFamily>(
            new SharpClawActionKey("jobs.logs.read"), "jobs.logs.read", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.AuditReadFamily>(
            new SharpClawActionKey("jobs.audit.read"), "jobs.audit.read", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ArtifactReadFamily>(
            new SharpClawActionKey("jobs.artifact.read"), "jobs.artifact.read", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.EventDeliverFamily>(
            new SharpClawActionKey("jobs.event.deliver"), "jobs.event.deliver", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.StateTransitionFamily>(
            new SharpClawActionKey("jobs.state.transition"), "jobs.state.transition", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.StateTransitionPrepareFamily>(
            new SharpClawActionKey("jobs.state.transition.prepare"), "jobs.state.transition.prepare", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.StateTransitionCommitFamily>(
            new SharpClawActionKey("jobs.state.transition.commit"), "jobs.state.transition.commit", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.StateTransitionRollbackFamily>(
            new SharpClawActionKey("jobs.state.transition.rollback"), "jobs.state.transition.rollback", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.PersistenceFamily>(
            new SharpClawActionKey("jobs.persistence"), "jobs.persistence", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.PersistencePrepareFamily>(
            new SharpClawActionKey("jobs.persistence.prepare"), "jobs.persistence.prepare", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.PersistenceCommitFamily>(
            new SharpClawActionKey("jobs.persistence.commit"), "jobs.persistence.commit", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.PersistenceRollbackFamily>(
            new SharpClawActionKey("jobs.persistence.rollback"), "jobs.persistence.rollback", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.InterruptionCheckFamily>(
            new SharpClawActionKey("jobs.interruption.check"), "jobs.interruption.check", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ExternalCallFamily>(
            new SharpClawActionKey("jobs.external_call"), "jobs.external_call", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.IrreversibleEffectFamily>(
            new SharpClawActionKey("jobs.irreversible_effect"), "jobs.irreversible_effect", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ExternalEffectPrepareFamily>(
            new SharpClawActionKey("jobs.external_effect.prepare"), "jobs.external_effect.prepare", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ExternalEffectReceiptFamily>(
            new SharpClawActionKey("jobs.external_effect.receipt"), "jobs.external_effect.receipt", CompleteJobsTerminal);
        await adapter.JobsActionBoundary.RunAsync<RuntimeJobsActionModule.ExternalEffectUncertainFamily>(
            new SharpClawActionKey("jobs.external_effect.uncertain"), "jobs.external_effect.uncertain", CompleteJobsTerminal);
    }

    private static ValueTask<object?> CompleteJobsTerminal(
        object? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(value);
    }

    [Test]
    public void Contract_manifest_rejects_a_declared_action_missing_from_the_graph()
    {
        var probe = new ModuleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(probe, workspace);
        var capture = new RuntimeModuleContractCapture("invalid-module");
        capture.Actions.Add(new RuntimeModuleActionDeclaration(
            capture.ModuleId,
            new SharpClawActionKey("module.missing.declared"),
            1,
            typeof(KernelActionEnvelope),
            typeof(object),
            false));

        Action action = () => RuntimeModuleContractManifest.Validate(
            adapter.Graph,
            [capture]);

        action.Should().Throw<KernelGraphCompilationException>()
            .WithMessage("*module.missing.declared*");
    }

    [Test]
    public void Contract_manifest_rejects_duplicate_declared_action_and_event_keys()
    {
        var probe = new ModuleProbe();
        using var workspace = new TemporaryWorkspace();
        var adapter = CreateAdapter(probe, workspace);
        var first = new RuntimeModuleContractCapture("first-module");
        var second = new RuntimeModuleContractCapture("second-module");
        first.Actions.Add(new RuntimeModuleActionDeclaration(
            first.ModuleId,
            DeclaredContractModule.ActionKey,
            1,
            typeof(KernelActionEnvelope),
            typeof(object),
            false));
        second.Actions.Add(new RuntimeModuleActionDeclaration(
            second.ModuleId,
            DeclaredContractModule.ActionKey,
            1,
            typeof(KernelActionEnvelope),
            typeof(object),
            false));
        first.Events.Add(new RuntimeModuleEventDeclaration(
            first.ModuleId,
            DeclaredContractModule.EventKey,
            1,
            typeof(DeclaredContractEvent),
            false));
        second.Events.Add(new RuntimeModuleEventDeclaration(
            second.ModuleId,
            DeclaredContractModule.EventKey,
            1,
            typeof(DeclaredContractEvent),
            false));

        Action duplicateActions = () => RuntimeModuleContractManifest.Validate(
            adapter.Graph,
            [first, second]);
        duplicateActions.Should().Throw<KernelGraphCompilationException>()
            .WithMessage("*duplicate declared keys*module.declared.action*");

        first.Actions.Clear();
        second.Actions.Clear();
        Action duplicateEvents = () => RuntimeModuleContractManifest.Validate(
            adapter.Graph,
            [first, second]);
        duplicateEvents.Should().Throw<KernelGraphCompilationException>()
            .WithMessage("*duplicate declared keys*module.declared.event*");
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
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null,
        IReadOnlyList<ISharpClawModule>? additionalModules = null,
        bool includeTool = false)
    {
        var provider = new ModuleProvider(includeTool);
        var module = new ModuleActionModule(provider, probe, includeTool);
        var modules = new List<ISharpClawModule> { module };
        if (additionalModules is not null)
            modules.AddRange(additionalModules);
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
        var moduleGrants = new Dictionary<
            string,
            IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(
            StringComparer.Ordinal)
        {
            [module.Identity.Id] = grants,
        };
        var declaredModules = additionalModules?.OfType<DeclaredContractModule>().ToArray()
            ?? Array.Empty<DeclaredContractModule>();
        foreach (var declaredModule in declaredModules)
        {
            var declaredGrants = SharpClawActionCatalog.All
                .ToDictionary(
                    key => key.Value,
                    key => KernelActionCatalog.DescriptorFor(key).Capabilities,
                    StringComparer.Ordinal);
            declaredGrants[DeclaredContractModule.ActionKey.Value] =
                ActionInterceptionCapabilities.Inspect |
                ActionInterceptionCapabilities.Wrap |
                ActionInterceptionCapabilities.Observe;
            foreach (var grant in CreateJobsGrants())
                declaredGrants[grant.Key] = grant.Value;
            moduleGrants[declaredModule.Identity.Id] = declaredGrants;
        }
        var eventModuleGrants = declaredModules
            .ToDictionary(
                declaredModule => declaredModule.Identity.Id,
                declaredModule =>
                {
                    var grants = new Dictionary<string, EventInterceptionCapabilities>(
                        StringComparer.Ordinal);
                    foreach (var descriptor in KernelActionLifecycleEvents.Descriptors)
                        grants[descriptor.Key.Value] = descriptor.Capabilities;
                    grants["runtime.event"] =
                        EventInterceptionCapabilities.Inspect |
                        EventInterceptionCapabilities.Replace |
                        EventInterceptionCapabilities.Cancel |
                        EventInterceptionCapabilities.Observe;
                    grants[DeclaredContractModule.EventKey.Value] =
                        EventInterceptionCapabilities.Inspect |
                        EventInterceptionCapabilities.Observe;
                    return (IReadOnlyDictionary<string, EventInterceptionCapabilities>)grants;
                },
                StringComparer.Ordinal);
        var sensitiveApprovals = declaredModules
            .SelectMany(declaredModule => SharpClawActionCatalog.Kernel
                .Select(key => (declaredModule, key)))
            .Where(value => KernelActionCatalog.DescriptorFor(value.key).ContainsSensitiveData)
            .Select(value =>
            {
                var descriptor = KernelActionCatalog.DescriptorFor(value.key).ToDescriptor();
                var types = KernelSchemaIdentity.ActionTypes(
                    descriptor,
                    typeof(KernelActionEnvelope),
                    typeof(object));
                return new KernelSensitiveActionApproval(
                    value.declaredModule.Identity.Id,
                    value.key,
                    descriptor.Version,
                    types.ActionType.AssemblyQualifiedName!,
                    types.ResultType.AssemblyQualifiedName!,
                    KernelSchemaIdentity.Action(
                        descriptor,
                        typeof(KernelActionEnvelope),
                        typeof(object)));
            })
            .Concat(declaredModules
                .SelectMany(declaredModule => CreateJobsApprovals(declaredModule.Identity.Id))
            )
            .ToArray();

        return new RuntimeKernelAdapter(
            configuration,
            new ServiceCollection().BuildServiceProvider(),
            new InMemoryConversationStore(),
            modules,
            workspace.CreateInstancePaths(),
            new ModuleProviderClientFactory(provider),
            new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = moduleGrants,
                EventModuleCapabilityGrants = eventModuleGrants,
                SensitiveActionApprovals = sensitiveApprovals,
            },
            repeatEvidenceAuthority);
    }

    private static IReadOnlyList<KernelSensitiveActionApproval> CreateJobsApprovals(
        string moduleId)
    {
        var jobsModule = new RuntimeJobsActionModule();
        var graphBuilder = new KernelGraphBuilder(includeStandardDefinitions: false);
        var moduleBuilder = new KernelModuleBuilder(graphBuilder, jobsModule.Identity);
        jobsModule.Configure(moduleBuilder);
        return jobsModule.Approvals
            .Select(approval => approval with { ModuleId = moduleId })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, ActionInterceptionCapabilities> CreateJobsGrants()
    {
        var jobsModule = new RuntimeJobsActionModule();
        var graphBuilder = new KernelGraphBuilder(includeStandardDefinitions: false);
        var moduleBuilder = new KernelModuleBuilder(graphBuilder, jobsModule.Identity);
        jobsModule.Configure(moduleBuilder);
        return jobsModule.Grants;
    }

    private static GatewayBackgroundActionBoundary CreateGatewayCoverageBoundary(
        GatewayCoverageProbe probe)
    {
        var module = new GatewayCoverageModule(probe);
        var registry = new KernelModuleRegistry();
        registry.Add(module);
        var graph = registry.Compile(options: new KernelGraphCompileOptions
        {
            ActionModuleCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(
                StringComparer.Ordinal)
            {
                [module.Identity.Id] = GatewayActionManifest.Published.ToDictionary(
                    static key => key.Value,
                    static key => KernelActionCatalog.DescriptorFor(key).Capabilities,
                    StringComparer.Ordinal),
            },
            SensitiveActionApprovals = GatewayActionManifest.Published
                .Where(key => KernelActionCatalog.DescriptorFor(key).ContainsSensitiveData)
                .Select(key =>
                {
                    var descriptor = KernelActionCatalog.DescriptorFor(key).ToDescriptor();
                    var types = KernelSchemaIdentity.ActionTypes(
                        descriptor,
                        typeof(KernelActionEnvelope),
                        typeof(object));
                    return new KernelSensitiveActionApproval(
                        module.Identity.Id,
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
        });
        var dispatcher = new KernelActionDispatcher(
            graph,
            new KernelActionExecutionContext(
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid()));
        return new GatewayBackgroundActionBoundary(graph, dispatcher);
    }

    private sealed record DeclaredContractEvent(string Value);

    private sealed class ClientCoverageProbe : ClientActionModuleSet.IClientActionContextSink
    {
        public ConcurrentQueue<string> ActionKeys { get; } = new();

        public void Observe(ActionContext<KernelActionEnvelope> context) =>
            ActionKeys.Enqueue(context.ActionKey.Value);
    }

    private sealed class GatewayCoverageProbe
    {
        public ConcurrentQueue<string> ActionKeys { get; } = new();
    }

    private sealed class GatewayCoverageModule(GatewayCoverageProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "roadmap-gateway-coverage",
            "Roadmap Gateway coverage",
            "roadmap-gateway");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton(probe);
            module.Services.AddSingleton<GatewayCoverageInterceptor>();
            foreach (var action in GatewayActionManifest.Published)
            {
                module.Hooks.For(action).Use<GatewayCoverageInterceptor>(new HookOrdering(
                    $"roadmap-gateway-{action.Value}",
                    HookPriority.Normal,
                    [],
                    [],
                    TimeSpan.FromSeconds(5),
                    HookFailurePolicy.FailAction));
            }
        }
    }

    private sealed class GatewayCoverageInterceptor(GatewayCoverageProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.ActionKeys.Enqueue(context.ActionKey.Value);
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class DeclaredContractModule(bool actionWildcardEnabled = true) : ISharpClawModule
    {
        public static readonly SharpClawActionKey ActionKey =
            new("module.declared.action");

        public static readonly SharpClawEventKey EventKey =
            new("module.declared.event");

        public ModuleIdentity Identity { get; } =
            new("declared-contract-test", "Declared contract test", "declared-contract");

        private int _wildcardCalls;
        public int WildcardCalls => Volatile.Read(ref _wildcardCalls);

        public ConcurrentQueue<string> WildcardKeys { get; } = new();

        public void ResetWildcardObservation()
        {
            while (WildcardKeys.TryDequeue(out _))
            {
            }

            Interlocked.Exchange(ref _wildcardCalls, 0);
        }

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Actions.Add(new ActionDescriptor<KernelActionEnvelope, object>(
                ActionKey,
                1,
                "module.declared",
                ActionInterceptionCapabilities.Inspect |
                ActionInterceptionCapabilities.Wrap |
                ActionInterceptionCapabilities.Observe,
                false,
                false,
                new ActionRepeatPolicy(
                    ActionRepeatKind.None,
                    1,
                    TimeSpan.Zero,
                    "module.declared.action"),
                null,
                TimeSpan.FromSeconds(5)));
            module.Events.Add(new EventDescriptor<DeclaredContractEvent>(
                EventKey,
                1,
                "module.declared",
                EventInterceptionCapabilities.Inspect |
                EventInterceptionCapabilities.Observe,
                false,
                false));
            module.Services.AddSingleton(this);
            module.Services.AddSingleton<DeclaredWildcardInterceptor>();
            module.Services.AddSingleton<DeclaredLifecycleInterceptor>();
            if (actionWildcardEnabled)
            {
                module.Hooks
                    .AnyAction()
                    .UseAny<DeclaredWildcardInterceptor>(new HookOrdering(
                        "declared-contract-wildcard",
                        HookPriority.Normal,
                        [],
                        [],
                        TimeSpan.FromSeconds(5),
                        HookFailurePolicy.FailAction));
            }

            module.Events
                .AnyEvent()
                .InterceptAny<DeclaredLifecycleInterceptor>(
                    new HookOrdering(
                        "declared-lifecycle-wildcard",
                        HookPriority.Normal,
                        [],
                        [],
                        TimeSpan.FromSeconds(5),
                        HookFailurePolicy.FailAction));
        }

        public ValueTask StartAsync(
            ModuleStartContext context,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        private sealed class DeclaredWildcardInterceptor(
            DeclaredContractModule owner) : IAnyActionInterceptor
        {
            public async ValueTask<IUntypedActionOutcome> InvokeAsync(
                UntypedActionContext context,
                IUntypedActionControl control,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._wildcardCalls);
                owner.WildcardKeys.Enqueue(context.Descriptor.Key.Value);
                return await control.ProceedAsync(cancellationToken);
            }
        }

        private sealed class DeclaredLifecycleInterceptor(
            DeclaredContractModule owner) : IAnyEventInterceptor
        {
            public ValueTask<IUntypedEventInterception> InterceptAsync(
                UntypedEventContext context,
                IUntypedEventControl control,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (context.Descriptor.Key == SharpClawEvents.ActionStarting)
                {
                    var action = context.Envelope.Payload
                        .GetProperty(nameof(KernelActionLifecycleEvent.ActionKey))
                        .GetProperty(nameof(SharpClawActionKey.Value))
                        .GetString();
                    if (string.IsNullOrWhiteSpace(action))
                        throw new AssertionException(
                            "The action lifecycle event has no action key.");

                    owner.WildcardKeys.Enqueue(action);
                    Interlocked.Increment(ref owner._wildcardCalls);
                }

                return ValueTask.FromResult(control.Continue());
            }
        }
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
        ModuleProbe probe,
        bool includeTool) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("module-action-test", "Module action test", "module-action");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<IProviderPlugin>(provider);
            module.Services.AddSingleton(new ModuleActionInterceptor(probe));
            if (includeTool)
            {
                module.Services.AddSingleton<ToolCoverageHandler>();
                module.Tools.Add<ToolCoverageHandler>(new ToolDescriptor(
                    "roadmap-tool",
                    "Runs the roadmap tool coverage path.",
                    ToolSchemas.EmptyObject));
            }
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

    private sealed class ModuleProvider(bool includeTool) : IProviderPlugin, IProviderApiClient
    {
        public string ProviderKey => "module-test";
        public string DisplayName => "Module test";
        public bool RequiresEndpoint => false;
        public bool RequiresApiKey => false;
        public bool SupportsNativeToolCalling => includeTool;
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

        public Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ToolAwareMessage> messages,
            IReadOnlyList<ChatToolDefinition> tools,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var hasToolResult = messages.Any(message =>
                string.Equals(message.Role, "tool", StringComparison.Ordinal));
            if (includeTool && !hasToolResult)
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    ToolCalls =
                    [
                        new ChatToolCall(
                            "roadmap-tool-call",
                            "roadmap-tool",
                            "{}")
                    ],
                    FinishReason = FinishReason.ToolCalls,
                    Usage = new TokenUsage(1, 1),
                });
            }

            return Task.FromResult(new ChatCompletionResult
            {
                Content = "module-tool-response",
                FinishReason = FinishReason.Stop,
                Usage = new TokenUsage(1, 1),
            });
        }
    }

    private sealed class ToolCoverageHandler : IToolHandler
    {
        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ToolResult.Text("roadmap-tool-response"));
        }
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
