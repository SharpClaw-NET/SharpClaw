using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Presentation;
using SharpClaw.Services;
using SharpClaw.Utils.Logging;

namespace SharpClaw.Tests.ClientUno;

[TestFixture]
public sealed class ClientUnoStartupSwitchTests
{
    [Test]
    public async Task Backend_enabled_attempts_to_start_bundled_runtime_host()
    {
        using var scope = TestScope.Create();
        var executable = scope.CreateExecutable(OperatingSystem.IsWindows()
            ? "SharpClaw.Runtime.Host.exe"
            : "SharpClaw.Runtime.Host");
        ProcessStartInfo? observed = null;

        using var manager = new BackendProcessManager(
            "http://127.0.0.1:48923",
            scope.Logs,
            frontendInstance: null,
            executablePath: executable,
            processOnPortProbe: () => false,
            apiReachabilityProbe: _ => Task.FromResult(false),
            processStartObserver: startInfo => observed = startInfo)
        {
            SkipLaunch = false,
        };

        await manager.EnsureStartedAsync();

        observed.Should().NotBeNull();
        observed!.FileName.Should().Be(executable);
        observed.WorkingDirectory.Should().Be(Path.GetDirectoryName(executable));
        observed.EnvironmentVariables["ASPNETCORE_URLS"].Should().Be("http://127.0.0.1:48923");
        manager.IsRunning.Should().BeTrue();
        manager.IsExternal.Should().BeFalse();
    }

    [Test]
    public async Task Backend_disabled_does_not_launch_bundled_runtime_host()
    {
        using var scope = TestScope.Create();
        var executable = scope.CreateExecutable(OperatingSystem.IsWindows()
            ? "SharpClaw.Runtime.Host.exe"
            : "SharpClaw.Runtime.Host");
        var launchAttempts = 0;

        using var manager = new BackendProcessManager(
            "http://127.0.0.1:48923",
            scope.Logs,
            frontendInstance: null,
            executablePath: executable,
            processOnPortProbe: () => false,
            apiReachabilityProbe: _ => Task.FromResult(false),
            processStartObserver: _ => launchAttempts++)
        {
            SkipLaunch = true,
        };

        var act = async () => await manager.EnsureStartedAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Backend launch is disabled*");
        launchAttempts.Should().Be(0);
        manager.IsRunning.Should().BeFalse();
        manager.IsExternal.Should().BeTrue();
    }

    [Test]
    public async Task Gateway_enabled_attempts_to_start_bundled_gateway()
    {
        using var scope = TestScope.Create();
        var executable = scope.CreateExecutable(OperatingSystem.IsWindows()
            ? "SharpClaw.Gateway.exe"
            : "SharpClaw.Gateway");
        ProcessStartInfo? observed = null;

        using var manager = new GatewayProcessManager(
            "http://0.0.0.0:48924",
            "http://127.0.0.1:48923",
            scope.Logs,
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

        await manager.EnsureStartedAsync();

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
    public async Task BootModel_skips_gateway_step_when_gateway_launch_is_disabled()
    {
        using var scope = TestScope.Create();
        var launchAttempts = 0;

        using var backend = new BackendProcessManager(
            "http://127.0.0.1:48923",
            scope.Logs,
            frontendInstance: null,
            executablePath: scope.CreateExecutable("SharpClaw.Runtime.Host.exe"),
            processOnPortProbe: () => false,
            apiReachabilityProbe: _ => Task.FromResult(false),
            processStartObserver: _ => { });

        using var gateway = new GatewayProcessManager(
            "http://0.0.0.0:48924",
            "http://127.0.0.1:48923",
            scope.Logs,
            frontendInstance: null,
            executablePath: scope.CreateExecutable("SharpClaw.Gateway.exe"),
            processOnPortProbe: () => false,
            gatewayReachabilityProbe: _ => Task.FromResult(false),
            processStartObserver: _ => launchAttempts++)
        {
            SkipLaunch = true,
        };

        using var api = new SharpClawApiClient("http://127.0.0.1:48923", scope.Logs);
        var boot = new BootModel(backend, gateway, api);

        var result = await boot.RunGatewayStepAsync(CancellationToken.None);

        result.Should().BeNull();
        launchAttempts.Should().Be(0);
        gateway.IsRunning.Should().BeFalse();
    }

    private sealed class TestScope : IDisposable
    {
        private readonly string _root;

        private TestScope(string root)
        {
            _root = root;
            Logs = new SessionLogWriter(
                "client-uno-startup-tests",
                logsRootDirectory: root,
                flushInterval: TimeSpan.FromHours(1));
        }

        public SessionLogWriter Logs { get; }

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
            Logs.Dispose();

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
