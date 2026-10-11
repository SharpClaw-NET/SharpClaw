using SharpClaw.Configuration;
using SharpClaw.Core.Kernel;
using SharpClaw.Presentation;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
[NonParallelizable]
internal sealed class LocalConfigurationOperationTests
{
    [TestCase("provider", true), TestCase("provider", false)]
    [TestCase("module", true), TestCase("module", false)]
    public async Task AnAdmittedLocalChangeRejectsCompetingRemoteChangesUntilItsWorkSettlesAsync(string localOperation, bool connect)
    {
        var probe = new BackendMutationStageProbe(OperationName(localOperation), BundledBackendMutationStage.BeforeCommit);
        var mutations = new BundledBackendMutationCoordinator(probe.ObserveAsync);
        await using var mutationDisposal = mutations.ConfigureAwait(false);
        var scope = new RemoteConnectionTestScope(mutations: mutations);
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var local = RunLocalChangeAsync(scope, localOperation, TestContext.CurrentContext.CancellationToken);
        try
        {
            await probe.WaitForEntryAsync().ConfigureAwait(false);
            var wasRunning = scope.Backend.IsRunning;
            Func<Task> competing = () => connect
                ? scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, "peer-secret", TestContext.CurrentContext.CancellationToken)
                : scope.Service.DisconnectAsync(TestContext.CurrentContext.CancellationToken);
            await competing.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
            scope.Backend.IsRunning.Should().Be(wasRunning);
            scope.BackendStarts.Should().Be(1);
            scope.PeerRequests.Should().BeEmpty();
            scope.Api.BaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
            (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
        }
        finally
        {
            probe.Release();
            await local.ConfigureAwait(false);
        }
        AssertLocalValues(scope, localOperation);
        var paired = await scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, "peer-secret",
            TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        paired.IsConfigured.Should().BeTrue();
        paired.IsConnected.Should().BeTrue();
        AssertLocalValues(scope, localOperation);
        probe.Calls.Should().Be(1);
    }

    [TestCase("provider"), TestCase("module")]
    public async Task ARemotePairingAdmittedFirstRejectsLocalWritesAndStopsAsync(string localOperation)
    {
        var scope = new RemoteConnectionTestScope { PausePeer = true };
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var remote = scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, "peer-secret", TestContext.CurrentContext.CancellationToken);
        try
        {
            await scope.PeerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            Func<Task> competing = () => RunLocalChangeAsync(scope, localOperation, TestContext.CurrentContext.CancellationToken);
            await competing.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
            remote.IsCompleted.Should().BeFalse();
            scope.Backend.IsRunning.Should().BeTrue();
            scope.BackendStarts.Should().Be(1);
            (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
        }
        finally
        {
            scope.PeerRelease.TrySetResult();
            await remote.ConfigureAwait(false);
        }
        using var configuration = scope.ReadConfiguration();
        configuration["RemoteBackend:Enabled"].Should().Be("true");
        configuration["RemoteBackend:AccessToken"].Should().Be("peer-secret");
        configuration["Provider:Key"].Should().BeNull();
        configuration["Packages:fixture"].Should().BeNull();
        configuration["Unrelated:Option"].Should().Be("preserved");
        scope.BackendStarts.Should().Be(2);
    }

    [TestCase("provider"), TestCase("module")]
    public async Task LocalPostcommitWorkRetainsOwnershipDuringCallerCancellationAndSharedShutdownAsync(string localOperation)
    {
        var physicalFailure = new IOException("Controlled secondary settlement failure.");
        var stage = new BackendMutationStageProbe(OperationName(localOperation), BundledBackendMutationStage.Committed, physicalFailure);
        var actions = new ConfigurationOperationProbe();
        var mutations = new BundledBackendMutationCoordinator(stage.ObserveAsync);
        await using var mutationDisposal = mutations.ConfigureAwait(false);
        var scope = new RemoteConnectionTestScope(actions.CreateDispatcher(), mutations);
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        var shutdown = new ClientShutdownCoordinator();
        var cleanup = new ControlledOperationGate();
        cleanup.Release();
        var local = RunLocalChangeAsync(scope, localOperation, caller.Token);
        Exception? receiptFailure = null;
        try
        {
            await stage.WaitForEntryAsync().ConfigureAwait(false);
            AssertLocalValues(scope, localOperation);
            await caller.CancelAsync().ConfigureAwait(false);
            await actions.WaitForPhaseAsync(OperationName(localOperation), ClientActionCatalog.CommandFail).ConfigureAwait(false);
            var first = shutdown.ShutdownAsync(mutations, scope.Actions, () => StopOwnedBackendAsync(scope, cleanup));
            var second = shutdown.ShutdownAsync(mutations, scope.Actions, () => StopOwnedBackendAsync(scope, cleanup));
            await AssertClosedAfterCommitAsync(scope, stage, local, first, second, cleanup, localOperation).ConfigureAwait(false);
        }
        finally
        {
            stage.Release();
            try { await caller.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try { await local.ConfigureAwait(false); }
                catch (KernelActionFailedException exception) { receiptFailure = exception; }
                finally
                {
                    await shutdown.ShutdownAsync(mutations, scope.Actions, () => StopOwnedBackendAsync(scope, cleanup)).ConfigureAwait(false);
                }
            }
        }
        AssertPrimaryCancellationReceipt(receiptFailure, physicalFailure);
        AssertLocalValues(scope, localOperation);
        scope.Service.IsProxyConfigured().Should().BeFalse();
        scope.PeerRequests.Should().BeEmpty();
        scope.BackendStarts.Should().Be(1);
        scope.Backend.IsRunning.Should().BeFalse();
        cleanup.Calls.Should().Be(1);
        stage.Calls.Should().Be(1);
    }

    [Test]
    public async Task ModuleInstallKeepsSharedOwnershipUntilItsActualRestartReadinessSettlesAsync()
    {
        var readiness = new ControlledOperationGate();
        var scope = new RemoteConnectionTestScope { StartupReadinessGate = readiness };
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        using var candidate = await scope.PrepareModuleAsync().ConfigureAwait(false);
        // The candidate acquisition is awaited. Concurrency proves restart ownership;
        // the nested finally releases readiness and joins the actual install task
        // before this using can retire candidate scratch or the scope can dispose.
#pragma warning disable CA2025
        var install = BundledModuleSetup.InstallAsync(scope.Frontend, scope.Backend, scope.Gateway, scope.Api, scope.Actions,
            scope.Modules, candidate, TestContext.CurrentContext.CancellationToken);
#pragma warning restore CA2025
        try
        {
            await readiness.WaitForEntryAsync().ConfigureAwait(false);
            candidate.IsInstalled.Should().BeTrue();
            scope.Modules.ReadInstalled().Should().ContainSingle().Which.Id.Should().Be("race_fixture");
            scope.BackendStarts.Should().Be(2);
            install.IsCompleted.Should().BeFalse();
            var committed = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            Func<Task> connect = () => scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, "peer-secret",
                TestContext.CurrentContext.CancellationToken);
            await connect.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
            Func<Task> retarget = () => scope.Api.UpdateBaseUrlAsync("http://127.0.0.1:48925/",
                TestContext.CurrentContext.CancellationToken).AsTask();
            await retarget.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
            scope.Api.BaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
            scope.PeerRequests.Should().BeEmpty();
            (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(committed);
        }
        finally
        {
            try { readiness.Release(); }
            finally { await install.ConfigureAwait(false); }
        }
        scope.Backend.IsRunning.Should().BeTrue();
        using var configuration = scope.ReadConfiguration();
        configuration["Packages:race_fixture"].Should().Be("true");
        configuration["RemoteBackend:Enabled"].Should().Be("false");
        configuration["Unrelated:Option"].Should().Be("preserved");
    }

    [Test]
    public async Task SharedShutdownRejectsRealBootStartupAndTargetChangesWithoutStartingAProcessAsync()
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        await scope.Service.DisposeAsync().ConfigureAwait(false);
        var boot = new BootModel(scope.Backend, scope.Gateway, scope.Api, scope.Frontend, scope.Actions);
        var result = await boot.RunBackendStepAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        result.Ok.Should().BeFalse();
        result.CanRetry.Should().BeFalse();
        Func<Task> retarget = () => boot.ApplyCustomUrlAsync("https://foreign.example/", TestContext.CurrentContext.CancellationToken).AsTask();
        await retarget.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false);
        scope.BackendStarts.Should().Be(0);
        scope.Backend.IsRunning.Should().BeFalse();
        scope.Api.BaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
        scope.Backend.ApiUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
        scope.Gateway.BackendBaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
        scope.PeerRequests.Should().BeEmpty();
        (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
    }

    [TestCase(false), TestCase(true)]
    public async Task ARejectedCloseReceiptStillRunsAndJoinsRequiredOwnerCleanupAsync(bool cleanupFails)
    {
        var probe = new ConfigurationOperationProbe("client.app.close");
        var scope = new RemoteConnectionTestScope(probe.CreateDispatcher());
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var cleanup = new ControlledOperationGate(cleanupFails ? new IOException("Controlled cleanup failure.") : null);
        var owner = new ClientShutdownCoordinator();
        var first = owner.ShutdownAsync(scope.Frontend.BackendMutations, scope.Actions, () => StopOwnedBackendAsync(scope, cleanup));
        Exception? receiptFailure = null;
        try
        {
            await cleanup.WaitForEntryAsync().ConfigureAwait(false);
            first.IsCompleted.Should().BeFalse();
            var second = owner.ShutdownAsync(scope.Frontend.BackendMutations, scope.Actions, () => StopOwnedBackendAsync(scope, cleanup));
            second.Should().BeSameAs(first);
            second.IsCompleted.Should().BeFalse();
            scope.Backend.IsRunning.Should().BeFalse();
            Func<Task> late = () => RunLocalChangeAsync(scope, "provider", TestContext.CurrentContext.CancellationToken);
            await late.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false);
        }
        finally
        {
            cleanup.Release();
            try { await first.ConfigureAwait(false); }
            catch (KernelActionFailedException exception) { receiptFailure = exception; }
        }
        var failure = receiptFailure.Should().BeOfType<KernelActionFailedException>().Which;
        failure.Error.Code.Should().Be(probe.Rejection.Code);
        failure.Error.Message.Should().Be(probe.Rejection.Message);
        cleanup.Calls.Should().Be(1);
        scope.BackendStarts.Should().Be(1);
        scope.PeerRequests.Should().BeEmpty();
    }

    [TestCase("provider", "true"), TestCase("provider", "invalid")]
    [TestCase("module", "true"), TestCase("module", "invalid")]
    public async Task ProtectedDocumentCallbackRejectsLocalWritesWhenTheDocumentIsPairedAsync(string localOperation, string enablement)
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(RemoteGatewayConnection.Create(RemoteConnectionTestScope.PeerAddress, "peer-secret"))
            .ConfigureAwait(false);
        var store = new SupprocomSecretFileStore(LocalEnvironment.CreateSecretsOptions(scope.BackendPaths.ConfigDirectory,
            false, scope.BackendPaths));
        await store.UpdateDocumentAsync(settings => settings.Select(setting =>
            string.Equals(setting.Key, "RemoteBackend:Enabled", StringComparison.Ordinal)
                ? new SupprocomSecretSetting(setting.Key, enablement) : setting).ToArray(),
            TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        Func<Task> write = () => store.UpdateDocumentAsync(settings => ApplyLocalSettings(scope, localOperation, settings),
            TestContext.CurrentContext.CancellationToken);
        await write.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
        using var configuration = scope.ReadConfiguration();
        configuration["RemoteBackend:Enabled"].Should().Be(enablement);
        configuration["RemoteBackend:AccessToken"].Should().Be("peer-secret");
        configuration["Unrelated:Option"].Should().Be("preserved");
        configuration["Provider:Key"].Should().BeNull();
        configuration["Packages:fixture"].Should().BeNull();
        scope.BackendStarts.Should().Be(0);
        scope.PeerRequests.Should().BeEmpty();
    }

    [TestCase("provider"), TestCase("module")]
    public async Task ADocumentPairedAfterLocalAdmissionRejectsTheWriteWithoutStoppingTheCurrentGenerationAsync(string localOperation)
    {
        var probe = new BackendMutationStageProbe(OperationName(localOperation), BundledBackendMutationStage.BeforeCommit);
        var mutations = new BundledBackendMutationCoordinator(probe.ObserveAsync);
        await using var mutationDisposal = mutations.ConfigureAwait(false);
        var scope = new RemoteConnectionTestScope(mutations: mutations);
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var local = RunLocalChangeAsync(scope, localOperation, TestContext.CurrentContext.CancellationToken);
        byte[] pairedBytes = [];
        KernelActionFailedException? failure = null;
        try
        {
            await probe.WaitForEntryAsync().ConfigureAwait(false);
            // Another authorized writer changed the document after local admission and its initial mode check.
            await scope.SaveConnectionAsync(RemoteGatewayConnection.Create(RemoteConnectionTestScope.PeerAddress, "peer-secret"))
                .ConfigureAwait(false);
            pairedBytes = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            probe.Release();
            try { await local.ConfigureAwait(false); }
            catch (KernelActionFailedException exception) { failure = exception; }
        }
        failure.Should().NotBeNull();
        (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(pairedBytes);
        scope.Backend.IsRunning.Should().BeTrue();
        scope.BackendStarts.Should().Be(1);
        scope.Api.BaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
        scope.PeerRequests.Should().BeEmpty();
        using var configuration = scope.ReadConfiguration();
        configuration["RemoteBackend:Enabled"].Should().Be("true");
        configuration["RemoteBackend:AccessToken"].Should().Be("peer-secret");
        configuration["Provider:Key"].Should().BeNull();
        configuration["Packages:fixture"].Should().BeNull();
        configuration["Unrelated:Option"].Should().Be("preserved");
    }

    private static IReadOnlyList<SupprocomSecretSetting> ApplyLocalSettings(RemoteConnectionTestScope scope,
        string operation, IReadOnlyList<SupprocomSecretSetting> settings) => operation switch
    {
        "provider" => BundledProviderSetup.UpdateSettings(settings, new("fixture-provider", "Fixture Provider", true, false),
            "fixture-model", null, "local-provider-secret"),
        "module" => BundledModuleSetup.UpdateSettings(settings, scope.Modules.ActiveRoot,
            [new InstalledModuleIdentity("fixture", "Fixture", "1.0.0")], false),
        _ => throw new AssertionException("Unknown local configuration operation."),
    };

    private static string OperationName(string operation) => operation switch
    {
        "provider" => "client.provider.configure",
        "module" => "client.module.enablement",
        _ => throw new AssertionException("Unknown local configuration operation."),
    };

    private static Task RunLocalChangeAsync(RemoteConnectionTestScope scope, string operation, CancellationToken token) => operation switch
    {
        "provider" => BundledProviderSetup.ApplyAsync(scope.Frontend, scope.Backend, scope.Gateway, scope.Api, scope.Actions,
            new("fixture-provider", "Fixture Provider", true, false), "fixture-model", null, "local-provider-secret", token),
        "module" => BundledModuleSetup.SetEnabledAsync(scope.Frontend, scope.Backend, scope.Gateway, scope.Api, scope.Actions,
            scope.Modules, new InstalledModuleIdentity("fixture", "Fixture", "1.0.0"), false, token),
        _ => throw new AssertionException("Unknown local configuration operation."),
    };

    private static void AssertLocalValues(RemoteConnectionTestScope scope, string operation)
    {
        using var configuration = scope.ReadConfiguration();
        configuration["Provider:Key"].Should().Be(string.Equals(operation, "provider", StringComparison.Ordinal) ? "fixture-provider" : null);
        configuration["Packages:fixture"].Should().Be(string.Equals(operation, "module", StringComparison.Ordinal) ? "false" : null);
        configuration["Unrelated:Option"].Should().Be("preserved");
    }

    private static void AssertPendingShutdown(Task local, Task first, Task second, ControlledOperationGate cleanup)
    {
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        local.IsCompleted.Should().BeFalse();
        cleanup.Calls.Should().Be(0, "the physical mutation must settle before its process owner is disposed");
    }

    private static async Task AssertClosedAfterCommitAsync(RemoteConnectionTestScope scope, BackendMutationStageProbe stage,
        Task local, Task first, Task second, ControlledOperationGate cleanup, string localOperation)
    {
        stage.IsHeldTokenCancelled.Should().BeFalse("committed process settlement owns its independent token");
        AssertPendingShutdown(local, first, second, cleanup);
        Func<Task> late = () => RunLocalChangeAsync(scope, localOperation, TestContext.CurrentContext.CancellationToken);
        await late.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false);
    }

    private static void AssertPrimaryCancellationReceipt(Exception? receiptFailure, IOException physicalFailure)
    {
        var failure = receiptFailure.Should().BeOfType<KernelActionFailedException>().Which;
        failure.Error.Code.Should().Be("ACTION_CONTINUATION_DENIED");
        failure.Message.Should().NotContain(physicalFailure.Message);
    }

    private static async Task StopOwnedBackendAsync(RemoteConnectionTestScope scope, ControlledOperationGate cleanup)
    {
        scope.Backend.Stop();
        await cleanup.RunAsync().ConfigureAwait(false);
    }
}
