using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Core.Kernel;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
[NonParallelizable]
internal sealed class RemoteConnectionSettingsTests
{
    [Test]
    public void ConnectionMergePreservesLocalSettingsAndDoesNotReuseThePreviousPeerCredential()
    {
        SupprocomSecretSetting[] original =
        [new("Provider:ApiKey", "local-provider-secret"), new("Database:Provider", "LocalStore"),
         new("Packages:local", "true"), new("RemoteBackend:Enabled", "true"),
         new("RemoteBackend:GatewayUrl", "https://old-peer.example/api/"), new("RemoteBackend:AccessToken", "old-peer-secret")];
        var connection = RemoteGatewayConnection.Create(new Uri("https://new-peer.example/"), null);
        var values = RemoteBackendConnectionService.UpdateSettings(original, connection)
            .ToDictionary(static setting => setting.Key, static setting => setting.Value, StringComparer.OrdinalIgnoreCase);
        values["RemoteBackend:GatewayUrl"].Should().Be("https://new-peer.example/api/");
        values.Should().NotContainKey("RemoteBackend:AccessToken");
        values["Provider:ApiKey"].Should().Be("local-provider-secret");
        values["Database:Provider"].Should().Be("LocalStore");
        values["Packages:local"].Should().Be("true");
        original.Should().Contain(setting => setting.Key == "RemoteBackend:AccessToken" && setting.Value == "old-peer-secret");
    }

    [TestCase(401), TestCase(503), TestCase(302)]
    public async Task FailedPeerProbePreservesProtectedConfigurationAndCurrentRuntimeAsync(int status)
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        scope.PeerStatus = (HttpStatusCode)status;
        Func<Task> connect = async () =>
            _ = await scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, "new-peer-secret", TestContext.CurrentContext.CancellationToken)
                .ConfigureAwait(false);
        await connect.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
        (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
        scope.Backend.IsRunning.Should().BeTrue();
        scope.BackendStarts.Should().Be(1);
        scope.PeerRequests.Should().ContainSingle();
        scope.PeerHandlers.Should().ContainSingle().Which.Disposals.Should().Be(1);
        scope.Api.BaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
    }

    [Test]
    public async Task ConnectUsesOnlyPeerBearerAndRestartsTheOwnedBackendWithoutRetargetingLocalClientsAsync()
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var result = await scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, "peer-secret", TestContext.CurrentContext.CancellationToken)
            .ConfigureAwait(false);
        result.IsConfigured.Should().BeTrue();
        result.IsConnected.Should().BeTrue();
        scope.BackendStarts.Should().Be(2);
        scope.Api.BaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
        scope.Backend.ApiUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
        scope.Gateway.BackendBaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
        scope.PeerRequests.Select(static request => request.Target.AbsolutePath)
            .Should().Equal("/tenant/api/health", "/tenant/api/ping", "/tenant/api/gateway/status");
        scope.PeerRequests.Should().OnlyContain(request => request.Authorization == "Bearer peer-secret"
            && !request.HasLocalKey && !request.HasGatewayToken && !request.HasCookie);
        scope.PeerHandlers.Should().ContainSingle().Which.Disposals.Should().Be(1);
        var bytes = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        Encoding.UTF8.GetString(bytes).Should().NotContain("peer-secret");
        using var configuration = scope.ReadConfiguration();
        configuration["RemoteBackend:Enabled"].Should().Be("true");
        configuration["RemoteBackend:AccessToken"].Should().Be("peer-secret");
        configuration["Unrelated:Option"].Should().Be("preserved");
        JsonSerializer.Serialize(result).Should().NotContain("peer-secret");
    }

    [Test]
    public async Task DisconnectWithNoLocalModulesStopsTheProxyAndLeavesOfflineConfigurationAsync()
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(RemoteGatewayConnection.Create(RemoteConnectionTestScope.PeerAddress, "old-peer-secret")).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        scope.Service.HasEnabledLocalModules().Should().BeFalse();
        var result = await scope.Service.DisconnectAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        result.IsConfigured.Should().BeFalse();
        scope.Backend.IsRunning.Should().BeFalse();
        scope.BackendStarts.Should().Be(1, "zero local modules must not bootstrap ordinary persistence");
        scope.PeerRequests.Should().BeEmpty();
        using var configuration = scope.ReadConfiguration();
        configuration["RemoteBackend:Enabled"].Should().Be("false");
        configuration["RemoteBackend:AccessToken"].Should().BeNull();
        configuration["RemoteBackend:GatewayUrl"].Should().BeNull();
        configuration["Unrelated:Option"].Should().Be("preserved");
    }

    [Test]
    public async Task LocalReadinessFailureKeepsTheAcceptedProxyBindingWithoutLocalFallbackAsync()
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(RemoteGatewayConnection.Create(RemoteConnectionTestScope.PeerAddress, "peer-secret")).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        scope.LocalReady = false;
        var result = await scope.Service.GetStatusAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        result.IsConfigured.Should().BeTrue();
        result.IsConnected.Should().BeFalse();
        result.HasError.Should().BeTrue();
        scope.Service.IsProxyConfigured().Should().BeTrue();
        scope.PeerRequests.Should().BeEmpty();
        scope.BackendStarts.Should().Be(1);
    }

    [Test]
    public async Task CallerCancellationBeforeCommitJoinsPeerWorkAndPreservesTheCurrentGenerationAsync()
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        scope.BlockPeer = true;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        var connect = scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, "peer-secret", caller.Token);
        Exception? failure = null;
        try
        {
            await scope.PeerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await caller.CancelAsync().ConfigureAwait(false);
            await scope.PeerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            connect.IsCompleted.Should().BeFalse("the physical peer transport is still settling");
        }
        finally
        {
            scope.PeerRelease.TrySetResult();
            await caller.CancelAsync().ConfigureAwait(false);
            failure = await TestTaskOutcome.CaptureAsync(connect).ConfigureAwait(false);
        }
        failure.Should().BeAssignableTo<OperationCanceledException>();
        (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
        scope.BackendStarts.Should().Be(1);
        scope.Backend.IsRunning.Should().BeTrue();
        scope.PeerHandlers.Should().ContainSingle().Which.Disposals.Should().Be(1);
    }

    [Test]
    public async Task AnArbitraryHealthyServerCannotBecomeAnAcceptedGatewayAsync()
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        scope.CompatiblePeer = false;
        var before = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        Func<Task> connect = async () =>
            _ = await scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, null, TestContext.CurrentContext.CancellationToken)
                .ConfigureAwait(false);
        await connect.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
        (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
        scope.BackendStarts.Should().Be(0);
        scope.Service.IsProxyConfigured().Should().BeFalse();
    }

    [TestCase("https://foreign.example/"), TestCase("http://127.0.0.1:48925/")]
    public async Task ABlockedPeerProbePreventsRetargetingUntilTheConnectionChangeSettlesAsync(string otherTarget)
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        scope.BlockPeer = true;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        var connect = scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, "peer-secret", caller.Token);
        Exception? failure = null;
        try
        {
            await scope.PeerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await AssertRetargetRejectedAsync(scope.Api, otherTarget).ConfigureAwait(false);
            scope.Api.BaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
            (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
        }
        finally
        {
            scope.PeerRelease.TrySetResult();
            await caller.CancelAsync().ConfigureAwait(false);
            failure = await TestTaskOutcome.CaptureAsync(connect).ConfigureAwait(false);
        }
        failure.Should().BeAssignableTo<OperationCanceledException>();
        scope.Service.IsProxyConfigured().Should().BeFalse();
        (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
        await scope.Api.UpdateBaseUrlAsync(otherTarget, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        scope.Api.BaseUrl.Should().Be(otherTarget, "the failed connection change has released its target ownership");
    }

    [TestCase("https://foreign.example/"), TestCase("http://127.0.0.1:48925/")]
    public async Task PersistedRemoteConfigurationPreventsTheApiCommitFromChangingTheLocalTargetAsync(string otherTarget)
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(RemoteGatewayConnection.Create(RemoteConnectionTestScope.PeerAddress, "peer-secret")).ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var api = new SharpClawApiClient(RemoteConnectionTestScope.LocalTarget, NullLogger<SharpClawApiClient>.Instance,
            scope.Frontend, scope.Actions);
        await using var apiDisposal = api.ConfigureAwait(false);
        await api.UpdateBaseUrlAsync(RemoteConnectionTestScope.LocalTarget, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        await AssertRetargetRejectedAsync(api, otherTarget).ConfigureAwait(false);
        api.BaseUrl.Should().Be(RemoteConnectionTestScope.LocalTarget);
        (await File.ReadAllBytesAsync(scope.ProtectedFile, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(before);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        await api.UpdateBaseUrlAsync(otherTarget, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        api.BaseUrl.Should().Be(otherTarget);
        scope.PeerRequests.Should().BeEmpty();
    }

    private static async Task AssertRetargetRejectedAsync(SharpClawApiClient api, string otherTarget)
    {
        Func<Task> retarget = () => api.UpdateBaseUrlAsync(otherTarget, TestContext.CurrentContext.CancellationToken).AsTask();
        await retarget.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
    }

    [Test]
    public async Task RepeatedServiceShutdownJoinsTheActivePeerTransportAndClosesAdmissionAsync()
    {
        var scope = new RemoteConnectionTestScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        await scope.SaveConnectionAsync(null).ConfigureAwait(false);
        scope.BlockPeer = true;
        var connect = scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, "peer-secret", TestContext.CurrentContext.CancellationToken);
        Task first = Task.CompletedTask;
        Task second = Task.CompletedTask;
        try
        {
            await scope.PeerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            first = scope.Service.DisposeAsync().AsTask();
            second = scope.Service.DisposeAsync().AsTask();
            await scope.PeerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse();
            connect.IsCompleted.Should().BeFalse();
        }
        finally
        {
            scope.PeerRelease.TrySetResult();
            _ = await TestTaskOutcome.CaptureAsync(connect).ConfigureAwait(false);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        }
        scope.BackendStarts.Should().Be(0);
        scope.Service.IsProxyConfigured().Should().BeFalse();
        Func<Task> rejected = () => scope.Service.ConnectAsync(RemoteConnectionTestScope.PeerAddress, null,
            TestContext.CurrentContext.CancellationToken);
        await rejected.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false);
        scope.PeerHandlers.Should().ContainSingle().Which.Disposals.Should().Be(1);
    }
}
