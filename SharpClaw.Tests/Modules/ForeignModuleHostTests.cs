using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ForeignModuleHostTests
{
    [Test]
    public async Task StartAsyncLaunchesSidecarAndPassesStartupEnvironment()
    {
        using var workspace = TestWorkspace.Create();
        await using var host = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace, "normal"));

        host.ProcessId.Should().BeGreaterThan(0);
        host.Handshake.ModuleId.Should().Be("sample_node_module");
        host.Handshake.Runtime.Should().Be(ModuleManifestRuntimeInfo.Node);
        host.Endpoints.Should().Contain(e => e.RoutePattern == "/modules/sample/ping");
        host.CapturedOutput.StandardOutput.Should().Contain($"moduleDir={workspace.ModuleDir}");
        host.CapturedOutput.StandardOutput.Should().Contain($"dataDir={workspace.DataDir}");
        host.CapturedOutput.StandardOutput.Should().Contain("token=run-token");
        host.CapturedOutput.StandardOutput.Should().Contain("moduleId=sample_node_module");
        host.CapturedOutput.StandardOutput.Should().Contain("runtime=node");

        await host.Module.InitializeAsync(host.Services, CancellationToken.None);
        var health = await host.Module.HealthCheckAsync(CancellationToken.None);
        health.IsHealthy.Should().BeTrue();

        await host.Module.ShutdownAsync();
        host.HasExited.Should().BeTrue();
    }

    [Test]
    public async Task StartAsyncPassesHostCapabilityEnvironmentWhenHostServicesAreAvailable()
    {
        using var workspace = TestWorkspace.Create();
        await using var hostServices = new ServiceCollection().BuildServiceProvider();
        await using var host = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace, "normal") with
            {
                HostServices = hostServices,
            });

        host.CapturedOutput.StandardOutput.Should().Contain("hostCapabilities=http://127.0.0.1:");
        host.CapturedOutput.StandardOutput.Should().Contain("hostCapabilitiesToken=");
    }

    [Test]
    public async Task StartAsyncReportsEarlyProcessExitWithCapturedOutput()
    {
        using var workspace = TestWorkspace.Create();
        var options = CreateLaunchOptions(workspace, "early-exit") with
        {
            StartupTimeout = TimeSpan.FromSeconds(2),
        };

        var act = async () => await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            options);

        var ex = await act.Should()
            .ThrowAsync<ForeignModuleStartupException>();
        ex.Which.Message.Should().Contain("exited with code 23 before readiness");
        ex.Which.Output.StandardOutput.Should().Contain("sidecar stdout before early exit");
        ex.Which.Output.StandardError.Should().Contain("sidecar stderr before early exit");
    }

    [Test]
    public async Task StartAsyncTimesOutWhenSidecarNeverBecomesReady()
    {
        using var workspace = TestWorkspace.Create();
        var options = CreateLaunchOptions(workspace, "never-ready") with
        {
            StartupTimeout = TimeSpan.FromMilliseconds(500),
        };

        var act = async () => await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            options);

        var ex = await act.Should()
            .ThrowAsync<ForeignModuleStartupException>();
        ex.Which.Message.Should().Contain("did not become ready within");
        ex.Which.Output.StandardOutput.Should().Contain("ENV|");
    }

    [Test]
    public async Task ShutdownKillsSidecarThatDoesNotExitAfterShutdownHook()
    {
        using var workspace = TestWorkspace.Create();
        await using var host = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace, "ignore-shutdown") with
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(300),
            });

        await host.Module.ShutdownAsync();

        host.HasExited.Should().BeTrue();
    }

    private static ModuleManifest Manifest() =>
        new(
            "sample_node_module",
            "Sample Node Module",
            "1.0.0",
            "snm",
            "dist/server.js",
            "0.0.0");

    private static ModuleManifestRuntimeInfo RuntimeInfo() =>
        new(ModuleManifestRuntimeInfo.Node, "dist/server.js");

    private static ForeignModuleHostLaunchOptions CreateLaunchOptions(
        TestWorkspace workspace,
        string mode)
    {
        var helperPath = ResolveSidecarHelperPath();
        return new ForeignModuleHostLaunchOptions
        {
            ExecutablePath = "dotnet",
            Arguments = [helperPath, "--mode", mode],
            WorkingDirectory = Path.GetDirectoryName(helperPath),
            ModuleDirectory = workspace.ModuleDir,
            ModuleDataDirectory = workspace.DataDir,
            ControlAddress = new Uri($"http://127.0.0.1:{GetFreeTcpPort()}"),
            ControlToken = "run-token",
            StartupTimeout = TimeSpan.FromSeconds(5),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            HostVersion = "0.1.0-beta",
            Environment = new Dictionary<string, string>
            {
                ["SHARPCLAW_TEST_TOOL_PREFIX"] = "snm",
            },
        };
    }

    private static string ResolveSidecarHelperPath()
    {
        var root = ResolveRepoRoot();
        var configuration = Directory.GetParent(TestContext.CurrentContext.TestDirectory)!.Name;
        var helperPath = Path.Combine(
            root,
            "SharpClaw.Tests",
            "Fixtures",
            "ForeignSidecar",
            "bin",
            configuration,
            "net10.0",
            "SharpClaw.TestFixtures.ForeignSidecar.dll");

        File.Exists(helperPath).Should().BeTrue(
            $"foreign sidecar helper must be built before tests run: '{helperPath}'");
        return helperPath;
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SharpClaw repository root.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root)
        {
            Root = root;
            ModuleDir = Path.Combine(root, "module");
            DataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(ModuleDir);
            Directory.CreateDirectory(DataDir);
        }

        public string Root { get; }
        public string ModuleDir { get; }
        public string DataDir { get; }

        public static TestWorkspace Create() =>
            new(Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "foreign-host",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
