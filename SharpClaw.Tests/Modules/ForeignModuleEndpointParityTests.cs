using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Clients;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Runtime.Host.Routing;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ForeignModuleEndpointParityTests
{
    [Test]
    public async Task JsonRoutesMatchBetweenInProcessAndOutOfProcessDotNetModules()
    {
        await using var inProcess = await StartAsync(ModuleHostKind.InProcess);
        await using var outOfProcess = await StartAsync(ModuleHostKind.OutOfProcess);

        var expected = await ReadJsonSurfaceAsync(inProcess.Api.BaseAddress);
        var actual = await ReadJsonSurfaceAsync(outOfProcess.Api.BaseAddress);

        actual.Should().Be(expected);
    }

    [Test]
    public async Task StaticAndStreamingRoutesMatchBetweenDotNetHostModes()
    {
        await using var inProcess = await StartAsync(ModuleHostKind.InProcess);
        await using var outOfProcess = await StartAsync(ModuleHostKind.OutOfProcess);

        var expected = await ReadAssetSurfaceAsync(inProcess.Api.BaseAddress);
        var actual = await ReadAssetSurfaceAsync(outOfProcess.Api.BaseAddress);

        actual.Should().Be(expected);
    }

    [Test]
    public async Task WebSocketRoutesMatchBetweenDotNetHostModes()
    {
        await using var inProcess = await StartAsync(ModuleHostKind.InProcess);
        await using var outOfProcess = await StartAsync(ModuleHostKind.OutOfProcess);

        var expected = await ReadWebSocketSurfaceAsync(inProcess.Api.BaseAddress);
        var actual = await ReadWebSocketSurfaceAsync(outOfProcess.Api.BaseAddress);

        actual.Should().Be(expected);
    }

    [Test]
    public async Task UnregisterMakesBothDotNetHostModesUnavailable()
    {
        foreach (var hostKind in Enum.GetValues<ModuleHostKind>())
        {
            await using var host = await StartAsync(hostKind);
            host.Registry.Unregister(host.Module.Id);

            using var client = new HttpClient { BaseAddress = host.Api.BaseAddress };
            using var response = await client.GetAsync("/modules/sample/ping");

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
    }

    [Test]
    public async Task HealthMatchesBetweenDotNetHostModes()
    {
        await using var inProcess = await StartAsync(ModuleHostKind.InProcess);
        await using var outOfProcess = await StartAsync(ModuleHostKind.OutOfProcess);

        var expected = await inProcess.Module.HealthCheckAsync(CancellationToken.None);
        var actual = await outOfProcess.Module.HealthCheckAsync(CancellationToken.None);

        actual.Should().BeEquivalentTo(expected);
    }

    private static async Task<JsonSurface> ReadJsonSurfaceAsync(Uri baseAddress)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        using var pingRequest = new HttpRequestMessage(HttpMethod.Get, "/modules/sample/ping?value=42");
        pingRequest.Headers.TryAddWithoutValidation("X-Test-Marker", "from-host");
        using var pingResponse = await client.SendAsync(pingRequest);

        using var echoContent = new StringContent(
            """{"hello":"world"}""",
            Encoding.UTF8,
            "application/json");
        using var echoResponse = await client.PostAsync("/modules/sample/echo?mode=body", echoContent);
        var ping = await ReadJsonAsync(pingResponse);
        var echo = await ReadJsonAsync(echoResponse);

        pingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        echoResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        return new JsonSurface(
            ping.GetProperty("ok").GetBoolean(),
            ping.GetProperty("path").GetString()!,
            ping.GetProperty("query").GetString()!,
            ping.GetProperty("marker").GetString()!,
            echo.GetProperty("method").GetString()!,
            echo.GetProperty("path").GetString()!,
            echo.GetProperty("query").GetString()!,
            echo.GetProperty("body").GetString()!,
            echo.GetProperty("contentType").GetString()!);
    }

    private static async Task<AssetSurface> ReadAssetSurfaceAsync(Uri baseAddress)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        using var staticResponse = await client.GetAsync("/modules/sample/static/hello.txt");
        using var streamResponse = await client.GetAsync("/modules/sample/stream");

        staticResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        streamResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        return new AssetSurface(
            await staticResponse.Content.ReadAsStringAsync(),
            staticResponse.Content.Headers.ContentType?.MediaType ?? "",
            await streamResponse.Content.ReadAsStringAsync(),
            streamResponse.Content.Headers.ContentType?.MediaType ?? "");
    }

    private static async Task<string> ReadWebSocketSurfaceAsync(Uri baseAddress)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(ToWebSocketUri(baseAddress, "/modules/sample/ws"), CancellationToken.None);

        var payload = Encoding.UTF8.GetBytes("hello");
        await socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None);

        result.MessageType.Should().Be(WebSocketMessageType.Text);
        return text;
    }

    private static Uri ToWebSocketUri(Uri baseAddress, string path) =>
        new UriBuilder(new Uri(baseAddress, path))
        {
            Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        }.Uri;

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<ParityHost> StartAsync(ModuleHostKind hostKind)
    {
        var registry = new ModuleRegistry();
        TestWorkspace? workspace = null;
        IAsyncDisposable? runtimeHost = null;
        ISharpClawRuntimeModule module;

        if (hostKind == ModuleHostKind.InProcess)
        {
            module = new InProcessParityModule();
            registry.Register(module);
        }
        else
        {
            workspace = TestWorkspace.Create();
            var foreignHost = await ForeignModuleHost.StartAsync(
                Manifest(),
                RuntimeInfo(),
                CreateLaunchOptions(workspace));
            runtimeHost = foreignHost;
            module = foreignHost.Module.Should().BeAssignableTo<ISharpClawRuntimeModule>().Subject;
            registry.Register(module, foreignHost);
        }

        var api = await StartApiAsync(registry, hostKind == ModuleHostKind.InProcess);
        return new ParityHost(registry, module, api, runtimeHost, workspace);
    }

    private static async Task<TestApiHost> StartApiAsync(
        ModuleRegistry registry,
        bool mapInProcessEndpoints)
    {
        var port = GetFreeTcpPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress.ToString());
        builder.Services.AddSingleton(registry);
        var app = builder.Build();
        app.UseWebSockets();

        if (mapInProcessEndpoints)
        {
            foreach (var module in registry.GetAllModules().OfType<ISharpClawRuntimeModule>())
                module.MapEndpoints(app);
        }
        else
        {
            app.MapForeignModuleEndpoints(registry);
        }

        await app.StartAsync();
        return new TestApiHost(app, baseAddress);
    }

    private static ModuleManifest Manifest() =>
        new(
            "sample_dotnet_module",
            "Sample .NET Module",
            "1.0.0",
            "sdm",
            "SharpClaw.TestFixtures.ForeignSidecar.dll",
            "0.0.0");

    private static ModuleManifestRuntimeInfo RuntimeInfo() =>
        ModuleManifestRuntimeInfo.FromJson("""
        {
          "runtime": "dotnet",
          "hostMode": "sidecar",
          "entryAssembly": "SharpClaw.TestFixtures.ForeignSidecar.dll"
        }
        """);

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
                ["SHARPCLAW_TEST_TOOL_PREFIX"] = "sdm",
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

    private enum ModuleHostKind
    {
        InProcess,
        OutOfProcess,
    }

    private sealed record JsonSurface(
        bool PingOk,
        string PingPath,
        string PingQuery,
        string PingMarker,
        string EchoMethod,
        string EchoPath,
        string EchoQuery,
        string EchoBody,
        string EchoContentType);

    private sealed record AssetSurface(
        string StaticBody,
        string StaticMediaType,
        string StreamBody,
        string StreamMediaType);

    private sealed record TestApiHost(WebApplication App, Uri BaseAddress) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class ParityHost(
        ModuleRegistry registry,
        ISharpClawRuntimeModule module,
        TestApiHost api,
        IAsyncDisposable? runtimeHost,
        TestWorkspace? workspace) : IAsyncDisposable
    {
        public ModuleRegistry Registry => registry;
        public ISharpClawRuntimeModule Module => module;
        public TestApiHost Api => api;

        public async ValueTask DisposeAsync()
        {
            await api.DisposeAsync();
            if (runtimeHost is not null)
                await runtimeHost.DisposeAsync();
            workspace?.Dispose();
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
                "foreign-parity",
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

    private sealed class InProcessParityModule : ISharpClawRuntimeModule
    {
        public string Id => "sample_dotnet_module";
        public string DisplayName => "Sample .NET Module";
        public string ToolPrefix => "sdm";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ModuleHealthStatus> HealthCheckAsync(CancellationToken ct) =>
            Task.FromResult(new ModuleHealthStatus(IsHealthy: true, Message: "ready"));

        public void MapEndpoints(object app)
        {
            var endpoints = (IEndpointRouteBuilder)app;
            endpoints.MapGet("/modules/sample/ping", async context =>
            {
                if (!await EnsureAvailableAsync(context))
                    return;

                await context.Response.WriteAsJsonAsync(new
                {
                    ok = true,
                    path = context.Request.Path.Value,
                    query = context.Request.QueryString.Value,
                    marker = context.Request.Headers["X-Test-Marker"].FirstOrDefault(),
                });
            });

            endpoints.MapPost("/modules/sample/echo", async context =>
            {
                if (!await EnsureAvailableAsync(context))
                    return;

                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                await context.Response.WriteAsJsonAsync(new
                {
                    method = context.Request.Method,
                    path = context.Request.Path.Value,
                    query = context.Request.QueryString.Value,
                    body = await reader.ReadToEndAsync(context.RequestAborted),
                    contentType = context.Request.ContentType,
                });
            });

            endpoints.MapGet("/modules/sample/static/hello.txt", async context =>
            {
                if (!await EnsureAvailableAsync(context))
                    return;

                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("static-parity-asset", context.RequestAborted);
            });

            endpoints.MapGet("/modules/sample/stream", async context =>
            {
                if (!await EnsureAvailableAsync(context))
                    return;

                context.Response.ContentType = "application/x-ndjson; charset=utf-8";
                await context.Response.WriteAsync(
                    "{\"delta\":\"first:\"}\n{\"delta\":\"second\"}\n{\"isFinal\":true}\n",
                    context.RequestAborted);
            });

            endpoints.MapGet("/modules/sample/ws", async context =>
            {
                if (!await EnsureAvailableAsync(context))
                    return;

                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("WebSocket connections only.", context.RequestAborted);
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[1024];
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var response = Encoding.UTF8.GetBytes("sidecar:" + text);
                await socket.SendAsync(
                    response,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    context.RequestAborted);

                while (socket.State == WebSocketState.Open)
                {
                    result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType != WebSocketMessageType.Close)
                        continue;

                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "closing",
                        CancellationToken.None);
                    break;
                }
            });
        }

        private static async Task<bool> EnsureAvailableAsync(HttpContext context)
        {
            var registry = context.RequestServices.GetRequiredService<ModuleRegistry>();
            if (registry.GetModule("sample_dotnet_module") is not null)
                return true;

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Module 'sample_dotnet_module' is not available.",
            }, context.RequestAborted);
            return false;
        }
    }
}
