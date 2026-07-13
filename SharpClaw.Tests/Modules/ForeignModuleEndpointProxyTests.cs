using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ForeignModuleEndpointProxyTests
{
    [Test]
    public async Task DiscoveredEndpointIsReachableThroughSharpClawRoute()
    {
        using var workspace = TestWorkspace.Create();
        await using var foreignHost = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace, "normal"));
        var registry = new ModuleRegistry();
        registry.Register(foreignHost.Module, foreignHost);

        await using var api = await StartApiAsync(registry);
        using var client = new HttpClient { BaseAddress = api.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/modules/sample/ping?value=42");
        request.Headers.TryAddWithoutValidation("X-Test-Marker", "from-host");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Sidecar", out var sidecarHeaders).Should().BeTrue();
        sidecarHeaders!.Single().Should().Be("yes");
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("path").GetString().Should().Be("/modules/sample/ping");
        doc.RootElement.GetProperty("query").GetString().Should().Be("?value=42");
        doc.RootElement.GetProperty("marker").GetString().Should().Be("from-host");
    }

    [Test]
    public async Task ProxyForwardsRequestBodyAndContentHeaders()
    {
        using var workspace = TestWorkspace.Create();
        await using var foreignHost = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace, "normal"));
        var registry = new ModuleRegistry();
        registry.Register(foreignHost.Module, foreignHost);

        await using var api = await StartApiAsync(registry);
        using var client = new HttpClient { BaseAddress = api.BaseAddress };
        using var content = new StringContent("""{"hello":"world"}""", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/modules/sample/echo?mode=body", content);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        doc.RootElement.GetProperty("method").GetString().Should().Be("POST");
        doc.RootElement.GetProperty("path").GetString().Should().Be("/modules/sample/echo");
        doc.RootElement.GetProperty("query").GetString().Should().Be("?mode=body");
        doc.RootElement.GetProperty("body").GetString().Should().Be("""{"hello":"world"}""");
        doc.RootElement.GetProperty("contentType").GetString().Should().Contain("application/json");
    }

    [Test]
    public async Task RouteReturnsUnavailableAfterModuleIsUnregistered()
    {
        using var workspace = TestWorkspace.Create();
        await using var foreignHost = await ForeignModuleHost.StartAsync(
            Manifest(),
            RuntimeInfo(),
            CreateLaunchOptions(workspace, "normal"));
        var registry = new ModuleRegistry();
        registry.Register(foreignHost.Module, foreignHost);

        await using var api = await StartApiAsync(registry);
        registry.Unregister(foreignHost.Module.Id);

        using var client = new HttpClient { BaseAddress = api.BaseAddress };
        using var response = await client.GetAsync("/modules/sample/ping");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private static async Task<TestApiHost> StartApiAsync(ModuleRegistry registry)
    {
        var port = GetFreeTcpPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress.ToString());
        builder.Services.AddSingleton(registry);
        var app = builder.Build();
        app.MapForeignModuleEndpoints(registry);
        await app.StartAsync();
        return new TestApiHost(app, baseAddress);
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

    private sealed record TestApiHost(WebApplication App, Uri BaseAddress) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
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
                "foreign-proxy",
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
