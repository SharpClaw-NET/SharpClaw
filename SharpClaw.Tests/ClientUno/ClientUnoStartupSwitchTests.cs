using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SharpClaw.Presentation;
using SharpClaw.Services;
using System.Collections.Immutable;

namespace SharpClaw.Tests.ClientUno;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class ClientUnoStartupSwitchTests
{
    [Test]
    public async Task Backend_enabled_attempts_to_start_bundled_runtime_hostAsync()
    {
        using var scope = TestScope.Create();
        var executable = scope.CreateExecutable(OperatingSystem.IsWindows()
            ? "SharpClaw.Runtime.Host.exe"
            : "SharpClaw.Runtime.Host");
        ProcessStartInfo? observed = null;

        using var manager = new BackendProcessManager(
            "http://127.0.0.1:48923",
            NullLogger<BackendProcessManager>.Instance,
            frontendInstance: null,
            executablePath: executable,
            processOnPortProbe: () => false,
            apiReachabilityProbe: _ => Task.FromResult(false),
            processStartObserver: startInfo => observed = startInfo)
        {
            SkipLaunch = false,
        };

        await manager.EnsureStartedAsync().ConfigureAwait(false);

        observed.Should().NotBeNull();
        observed!.FileName.Should().Be(executable);
        observed.WorkingDirectory.Should().Be(Path.GetDirectoryName(executable));
        observed.EnvironmentVariables["ASPNETCORE_URLS"].Should().Be("http://127.0.0.1:48923");
        manager.IsRunning.Should().BeTrue();
        manager.IsExternal.Should().BeFalse();
    }

    [Test]
    public async Task Backend_disabled_does_not_launch_bundled_runtime_hostAsync()
    {
        using var scope = TestScope.Create();
        var executable = scope.CreateExecutable(OperatingSystem.IsWindows()
            ? "SharpClaw.Runtime.Host.exe"
            : "SharpClaw.Runtime.Host");
        var launchAttempts = 0;

        using var manager = new BackendProcessManager(
            "http://127.0.0.1:48923",
            NullLogger<BackendProcessManager>.Instance,
            frontendInstance: null,
            executablePath: executable,
            processOnPortProbe: () => false,
            apiReachabilityProbe: _ => Task.FromResult(false),
            processStartObserver: _ => launchAttempts++)
        {
            SkipLaunch = true,
        };

        var act = async () => await manager.EnsureStartedAsync().ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Backend launch is disabled*").ConfigureAwait(false);
        launchAttempts.Should().Be(0);
        manager.IsRunning.Should().BeFalse();
        manager.IsExternal.Should().BeTrue();
    }

    [Test]
    public async Task Gateway_enabled_attempts_to_start_bundled_gatewayAsync()
    {
        using var scope = TestScope.Create();
        var executable = scope.CreateExecutable(OperatingSystem.IsWindows()
            ? "SharpClaw.Gateway.exe"
            : "SharpClaw.Gateway");
        ProcessStartInfo? observed = null;

        using var manager = new GatewayProcessManager(
            "http://0.0.0.0:48924",
            "http://127.0.0.1:48923",
            NullLogger<GatewayProcessManager>.Instance,
            frontendInstance: null,
            executablePath: executable,
            processOnPortProbe: () => false,
            gatewayReachabilityProbe: _ => Task.FromResult(false),
            processStartObserver: startInfo => observed = startInfo)
        {
            SkipLaunch = false,
            ApiKey = "test-api-key",
            GatewayToken = "test-gateway-token",
        };

        await manager.EnsureStartedAsync().ConfigureAwait(false);

        observed.Should().NotBeNull();
        observed!.FileName.Should().Be(executable);
        observed.WorkingDirectory.Should().Be(Path.GetDirectoryName(executable));
        observed.EnvironmentVariables["ASPNETCORE_URLS"].Should().Be("http://0.0.0.0:48924");
        observed.EnvironmentVariables["InternalApi__BaseUrl"].Should().Be("http://127.0.0.1:48923");
        observed.ArgumentList.Should().Contain("--InternalApi:BaseUrl=http://127.0.0.1:48923");
        observed.ArgumentList.Should().Contain("--InternalApi:ApiKey=test-api-key");
        observed.ArgumentList.Should().Contain("--InternalApi:GatewayToken=test-gateway-token");
        manager.IsRunning.Should().BeTrue();
        manager.IsExternal.Should().BeFalse();
    }

    [Test]
    public async Task BootModel_skips_gateway_step_when_gateway_launch_is_disabledAsync()
    {
        using var scope = TestScope.Create();
        var launchAttempts = 0;

        using var backend = new BackendProcessManager(
            "http://127.0.0.1:48923",
            NullLogger<BackendProcessManager>.Instance,
            frontendInstance: null,
            executablePath: scope.CreateExecutable("SharpClaw.Runtime.Host.exe"),
            processOnPortProbe: () => false,
            apiReachabilityProbe: _ => Task.FromResult(false),
            processStartObserver: _ => { });

        using var gateway = new GatewayProcessManager(
            "http://0.0.0.0:48924",
            "http://127.0.0.1:48923",
            NullLogger<GatewayProcessManager>.Instance,
            frontendInstance: null,
            executablePath: scope.CreateExecutable("SharpClaw.Gateway.exe"),
            processOnPortProbe: () => false,
            gatewayReachabilityProbe: _ => Task.FromResult(false),
            processStartObserver: _ => launchAttempts++)
        {
            SkipLaunch = true,
        };

        using var api = new SharpClawApiClient(
            "http://127.0.0.1:48923",
            NullLogger<SharpClawApiClient>.Instance,
            frontendInstance: null,
            clientActions: new ClientActionDispatcher());
        var boot = new BootModel(backend, gateway, api, null, new ClientActionDispatcher());

        var result = await boot.RunGatewayStepAsync(CancellationToken.None).ConfigureAwait(false);

        result.Should().BeNull();
        launchAttempts.Should().Be(0);
        gateway.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task ExitedBundledBackendIsNotRetriedAndNeverKeepsARunningDiagnosticAsync()
    {
        using var scope = TestScope.Create();
        var launches = 0;
        using var backend = new BackendProcessManager("http://127.0.0.1:48923",
            NullLogger<BackendProcessManager>.Instance, null, scope.CreateExecutable("runtime"),
            () => false, _ => Task.FromResult(false), _ => launches++);
        using var gateway = new GatewayProcessManager("http://127.0.0.1:48924", backend.ApiUrl,
            NullLogger<GatewayProcessManager>.Instance)
        { SkipLaunch = true };
        using var api = new SharpClawApiClient(backend.ApiUrl, NullLogger<SharpClawApiClient>.Instance,
            null, new ClientActionDispatcher());
        var boot = new BootModel(backend, gateway, api, null, new ClientActionDispatcher());
        var start = await boot.RunBackendStepAsync(CancellationToken.None).ConfigureAwait(false);
        start.Ok.Should().BeTrue();
        backend.Stop();
        var echo = await boot.RunEchoStepAsync(CancellationToken.None).ConfigureAwait(false);
        echo.Ok.Should().BeFalse();
        echo.CanRetry.Should().BeFalse();
        boot.ShouldRetry(echo, 1).Should().BeFalse();
        boot.ShouldRetry(new StepResult(false, new DiagnosticLine("Ping", "timeout", true)), 1)
            .Should().BeFalse("a process which dies between echo and ping must not be restarted either");
        var diagnostics = boot.RefreshBackendDiagnostic(ImmutableArray.Create(start.Line, echo.Line));
        diagnostics[0].IsError.Should().BeTrue();
        diagnostics[0].Result.Should().Be("stopped (bundled)");
        diagnostics.Should().NotContain(line => line.Result.Contains("running", StringComparison.Ordinal));
        launches.Should().Be(1);
    }

    [Test]
    public void BundledProbeBudgetAllowsColdSidecarBootstrapWithoutUnlimitedRetries()
    {
        BootModel.BundledStartupProbeBudget.Should().Be(TimeSpan.FromSeconds(120));
        BootModel.MaxRetries.Should().Be(3);
    }

    [Test]
    public async Task RetryPolicyOnlyRetriesTransientFailuresWhileTheBackendIsAvailableAsync()
    {
        using var scope = TestScope.Create();
        using var backend = new BackendProcessManager("http://127.0.0.1:48923",
            NullLogger<BackendProcessManager>.Instance, null, scope.CreateExecutable("runtime"),
            () => false, _ => Task.FromResult(false), _ => { });
        using var gateway = new GatewayProcessManager("http://127.0.0.1:48924", backend.ApiUrl,
            NullLogger<GatewayProcessManager>.Instance)
        { SkipLaunch = true };
        using var api = new SharpClawApiClient(backend.ApiUrl, NullLogger<SharpClawApiClient>.Instance,
            null, new ClientActionDispatcher());
        var boot = new BootModel(backend, gateway, api, null, new ClientActionDispatcher());
        await backend.EnsureStartedAsync().ConfigureAwait(false);
        var transient = new StepResult(false, new DiagnosticLine("Echo", "timeout", true));
        boot.ShouldRetry(transient, 1).Should().BeTrue();
        boot.ShouldRetry(transient, 3).Should().BeFalse();
        boot.ShouldRetry(transient with { CanRetry = false }, 1).Should().BeFalse();
        boot.ShouldRetry(transient with { Ok = true }, 1).Should().BeFalse();
        boot.RefreshBackendDiagnostic(ImmutableArray.Create(new DiagnosticLine("Backend", "running (bundled)", false)))
            [0].IsError.Should().BeFalse();
    }

    private sealed class TestScope : IDisposable
    {
        private readonly string _root;

        private TestScope(string root)
        {
            _root = root;
        }

        public static TestScope Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "sharpclaw-client-uno-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestScope(root);
        }

        public string CreateExecutable(string fileName)
        {
            var path = Path.Combine(_root, fileName);
            File.WriteAllText(path, string.Empty);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test files.
            }
        }
    }
}
