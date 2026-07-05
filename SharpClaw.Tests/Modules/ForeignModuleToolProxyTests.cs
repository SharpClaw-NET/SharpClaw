using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ForeignModuleToolProxyTests
{
    [Test]
    public async Task DiscoveredToolsAreRegisteredAndExecuteThroughSidecar()
    {
        using var workspace = TestWorkspace.Create();
        await using var foreignHost = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace));
        var registry = new ModuleRegistry();
        registry.Register(foreignHost.Module, foreignHost);
        using var parameters = JsonDocument.Parse("""{"value":42}""");
        var job = new AgentJobContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ResourceId: null,
            ActionKey: "snm_sample_job");

        var toolDefinitions = registry.GetAllToolDefinitions();
        var result = await foreignHost.Module.ExecuteToolAsync(
            "sample_job",
            parameters.RootElement,
            job,
            foreignHost.Services,
            CancellationToken.None);

        registry.TryResolve("sample_job", out var moduleId, out var toolName).Should().BeTrue();
        moduleId.Should().Be("sample_node_module");
        toolName.Should().Be("sample_job");
        toolDefinitions.Should().Contain(tool => tool.Name == "sample_job");
        foreignHost.Module.GetToolDefinitions().Should().Contain(tool => tool.Name == "sample_job");
        result.Should().Be("""job:sample_job:{"value":42}""");
    }

    [Test]
    public async Task DiscoveredInlineToolsExecuteThroughSidecar()
    {
        using var workspace = TestWorkspace.Create();
        await using var foreignHost = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace));
        using var parameters = JsonDocument.Parse("""{"value":"abc"}""");
        var context = new InlineToolContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "call-1");

        var result = await foreignHost.Module.ExecuteInlineToolAsync(
            "sample_inline",
            parameters.RootElement,
            context,
            foreignHost.Services,
            CancellationToken.None);

        foreignHost.Module.GetInlineToolDefinitions().Should()
            .Contain(tool => tool.Name == "sample_inline");
        result.Should().Be("""inline:sample_inline:{"value":"abc"}""");
    }

    [Test]
    public async Task DiscoveredStreamingToolYieldsSidecarDeltas()
    {
        using var workspace = TestWorkspace.Create();
        await using var foreignHost = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace));
        using var parameters = JsonDocument.Parse("{}");
        var job = new AgentJobContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ResourceId: null,
            ActionKey: "snm_sample_stream");

        var stream = foreignHost.Module.ExecuteToolStreamingAsync(
            "sample_stream",
            parameters.RootElement,
            job,
            foreignHost.Services,
            CancellationToken.None);

        stream.Should().NotBeNull();
        var chunks = new List<string>();
        await foreach (var chunk in stream!)
            chunks.Add(chunk);

        foreignHost.Module.GetToolDefinitions().Should()
            .Contain(tool => tool.Name == "sample_stream");
        chunks.Should().Equal("first:", "second");
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

    private static ForeignModuleHostLaunchOptions CreateLaunchOptions(TestWorkspace workspace)
    {
        var helperPath = ResolveSidecarHelperPath();
        return new ForeignModuleHostLaunchOptions
        {
            ExecutablePath = "dotnet",
            Arguments = [helperPath, "--mode", "normal"],
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
                "foreign-tools",
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
