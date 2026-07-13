using System.Net;
using System.Text;
using System.Text.Json;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ForeignModuleProtocolClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task HandshakeSendsControlTokenAndValidatesManifestIdentity()
    {
        using var handler = new FakeSidecarHandler((_, _) => Json(new ForeignModuleHandshakeResponse(
            ForeignModuleProtocol.Version,
            "sample_node_module",
            "snm",
            ModuleManifestRuntimeInfo.Node,
            "v24.0.0",
            [
                ForeignModuleCapability.Endpoints,
                ForeignModuleCapability.LifecycleHooks,
            ])));
        using var httpClient = CreateHttpClient(handler);
        var client = new ForeignModuleProtocolClient(httpClient, "run-token");

        var response = await client.HandshakeAsync(
            Manifest(),
            new ModuleManifestRuntimeInfo(ModuleManifestRuntimeInfo.Node, "dist/server.js"),
            "0.1.0-beta");

        response.Runtime.Should().Be(ModuleManifestRuntimeInfo.Node);
        response.Capabilities.Should().Contain(ForeignModuleCapability.Endpoints);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Path.Should().Be(ForeignModuleProtocol.HandshakePath);
        handler.Requests[0].Token.Should().Be("run-token");

        var request = JsonSerializer.Deserialize<ForeignModuleHandshakeRequest>(
            handler.Requests[0].Body!,
            JsonOptions)!;
        request.ProtocolVersion.Should().Be(ForeignModuleProtocol.Version);
        request.ModuleId.Should().Be("sample_node_module");
        request.ToolPrefix.Should().Be("snm");
        request.HostVersion.Should().Be("0.1.0-beta");
    }

    [Test]
    public async Task HandshakeRejectsModuleIdentityMismatch()
    {
        using var handler = new FakeSidecarHandler((_, _) => Json(new ForeignModuleHandshakeResponse(
            ForeignModuleProtocol.Version,
            "wrong_module",
            "snm",
            ModuleManifestRuntimeInfo.Node,
            "v24.0.0")));
        using var httpClient = CreateHttpClient(handler);
        var client = new ForeignModuleProtocolClient(httpClient, "run-token");

        var act = async () => await client.HandshakeAsync(
            Manifest(),
            new ModuleManifestRuntimeInfo(ModuleManifestRuntimeInfo.Node, "dist/server.js"));

        await act.Should()
            .ThrowAsync<ForeignModuleProtocolException>()
            .WithMessage("*handshake id 'wrong_module'*manifest id 'sample_node_module'*");
    }

    [Test]
    public async Task DiscoverReadsEndpointDescriptors()
    {
        var permission = new ForeignModulePermissionDescriptor(
            IsPerResource: true,
            DelegateTo: "CanUpdateAgentJob");
        var endpoint = new ForeignModuleEndpointDescriptor(
            Method: "POST",
            RoutePattern: "/modules/sample/render",
            ResponseMode: ForeignModuleEndpointResponseMode.Json,
            AuthPolicy: "authenticated",
            Permission: permission,
            ContributionId: "sample.render");

        using var handler = new FakeSidecarHandler((_, _) => Json(new ForeignModuleDiscoveryResponse([endpoint])));
        using var httpClient = CreateHttpClient(handler);
        var client = new ForeignModuleProtocolClient(httpClient, "run-token");

        var discovery = await client.DiscoverAsync();

        discovery.Endpoints.Should().ContainSingle();
        var actual = discovery.Endpoints![0];
        actual.Method.Should().Be("POST");
        actual.RoutePattern.Should().Be("/modules/sample/render");
        actual.ResponseMode.Should().Be(ForeignModuleEndpointResponseMode.Json);
        actual.Permission.Should().Be(permission);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Path.Should().Be(ForeignModuleProtocol.DiscoveryPath);
        handler.Requests[0].Token.Should().Be("run-token");
    }

    [Test]
    public async Task LifecycleAndHealthUseControlPlanePaths()
    {
        using var handler = new FakeSidecarHandler((request, _) =>
            request.RequestUri!.AbsolutePath switch
            {
                ForeignModuleProtocol.HealthPath => Json(new ForeignModuleHealthResponse(
                    IsHealthy: true,
                    Message: "ready")),
                ForeignModuleProtocol.InitializePath => Json(new ForeignModuleLifecycleResponse(
                    Accepted: true,
                    Message: "initialized")),
                ForeignModuleProtocol.ShutdownPath => Json(new ForeignModuleLifecycleResponse(
                    Accepted: true,
                    Message: "stopped")),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        using var httpClient = CreateHttpClient(handler);
        var client = new ForeignModuleProtocolClient(httpClient, "run-token");

        var health = await client.HealthAsync();
        var initialized = await client.InitializeAsync(Manifest());
        var stopped = await client.ShutdownAsync(Manifest());

        health.ToModuleHealthStatus().IsHealthy.Should().BeTrue();
        initialized.Message.Should().Be("initialized");
        stopped.Message.Should().Be("stopped");
        handler.Requests.Select(r => r.Path).Should().Equal(
            ForeignModuleProtocol.HealthPath,
            ForeignModuleProtocol.InitializePath,
            ForeignModuleProtocol.ShutdownPath);
        handler.Requests.Should().OnlyContain(r => r.Token == "run-token");
    }

    [Test]
    public async Task ControlRequestFailureIncludesStatusAndBody()
    {
        using var handler = new FakeSidecarHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("bad token", Encoding.UTF8, "text/plain"),
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new ForeignModuleProtocolClient(httpClient, "run-token");

        var act = async () => await client.HealthAsync();

        var ex = await act.Should()
            .ThrowAsync<ForeignModuleProtocolException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ex.Which.ResponseBody.Should().Be("bad token");
    }

    private static ModuleManifest Manifest() =>
        new(
            "sample_node_module",
            "Sample Node Module",
            "1.0.0",
            "snm",
            "dist/server.js",
            "0.0.0");

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:49152"),
        };

    private static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        string? Token,
        string? Body);

    private sealed class FakeSidecarHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues(ForeignModuleProtocol.TokenHeaderName, out var tokens);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                tokens?.SingleOrDefault(),
                body));

            return responder(request, body);
        }
    }
}
