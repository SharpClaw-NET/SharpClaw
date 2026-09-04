using System.Net;
using System.Text;
using System.Text.Json;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ForeignRegistrationProtocolClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task HandshakeSendsControlTokenAndValidatesManifestIdentity()
    {
        using var handler = new FakeSidecarHandler((_, _) => Json(new ForeignRegistrationHandshakeResponse(
            ForeignRegistrationProtocol.Version,
            "sample_dotnet_registration",
            "sdm",
            PackageRuntimeInfo.DotNet,
            "net10.0",
            [
                ForeignRegistrationCapability.Endpoints,
                ForeignRegistrationCapability.LifecycleHooks,
            ])));
        using var httpClient = CreateHttpClient(handler);
        var client = new ForeignRegistrationProtocolClient(httpClient, "run-token");

        var response = await client.HandshakeAsync(
            Manifest(),
            new PackageRuntimeInfo(PackageRuntimeInfo.DotNet, "SharpClaw.TestFixtures.ForeignSidecar.dll"),
            "0.1.0-beta");

        response.Runtime.Should().Be(PackageRuntimeInfo.DotNet);
        response.Capabilities.Should().Contain(ForeignRegistrationCapability.Endpoints);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Path.Should().Be(ForeignRegistrationProtocol.HandshakePath);
        handler.Requests[0].Token.Should().Be("run-token");

        var request = JsonSerializer.Deserialize<ForeignRegistrationHandshakeRequest>(
            handler.Requests[0].Body!,
            JsonOptions)!;
        request.ProtocolVersion.Should().Be(ForeignRegistrationProtocol.Version);
        request.SourceId.Should().Be("sample_dotnet_registration");
        request.ToolPrefix.Should().Be("sdm");
        request.HostVersion.Should().Be("0.1.0-beta");
    }

    [Test]
    public async Task HandshakeRejectsModuleIdentityMismatch()
    {
        using var handler = new FakeSidecarHandler((_, _) => Json(new ForeignRegistrationHandshakeResponse(
            ForeignRegistrationProtocol.Version,
            "wrong_registration",
            "sdm",
            PackageRuntimeInfo.DotNet,
            "net10.0")));
        using var httpClient = CreateHttpClient(handler);
        var client = new ForeignRegistrationProtocolClient(httpClient, "run-token");

        var act = async () => await client.HandshakeAsync(
            Manifest(),
            new PackageRuntimeInfo(PackageRuntimeInfo.DotNet, "SharpClaw.TestFixtures.ForeignSidecar.dll"));

        await act.Should()
            .ThrowAsync<ForeignRegistrationProtocolException>()
            .WithMessage("*handshake id 'wrong_registration'*manifest id 'sample_dotnet_registration'*");
    }

    [Test]
    public async Task DiscoverReadsEndpointDescriptors()
    {
        var permission = new ForeignRegistrationPermissionDescriptor(
            IsPerResource: true,
            DelegateTo: "CanUpdateAgentJob");
        var endpoint = new ForeignEndpointDescriptor(
            Method: "POST",
            RoutePattern: "/contributions/sample/render",
            ResponseMode: ForeignEndpointResponseMode.Json,
            AuthPolicy: "authenticated",
            Permission: permission,
            ContributionId: "sample.render");

        using var handler = new FakeSidecarHandler((_, _) => Json(new ForeignRegistrationDiscoveryResponse([endpoint])));
        using var httpClient = CreateHttpClient(handler);
        var client = new ForeignRegistrationProtocolClient(httpClient, "run-token");

        var discovery = await client.DiscoverAsync();

        discovery.Endpoints.Should().ContainSingle();
        var actual = discovery.Endpoints![0];
        actual.Method.Should().Be("POST");
        actual.RoutePattern.Should().Be("/contributions/sample/render");
        actual.ResponseMode.Should().Be(ForeignEndpointResponseMode.Json);
        actual.Permission.Should().Be(permission);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Path.Should().Be(ForeignRegistrationProtocol.DiscoveryPath);
        handler.Requests[0].Token.Should().Be("run-token");
    }

    [Test]
    public async Task LifecycleAndHealthUseControlPlanePaths()
    {
        using var handler = new FakeSidecarHandler((request, _) =>
            request.RequestUri!.AbsolutePath switch
            {
                ForeignRegistrationProtocol.HealthPath => Json(new ForeignRegistrationHealthResponse(
                    IsHealthy: true,
                    Message: "ready")),
                ForeignRegistrationProtocol.InitializePath => Json(new ForeignRegistrationLifecycleResponse(
                    Accepted: true,
                    Message: "initialized")),
                ForeignRegistrationProtocol.ShutdownPath => Json(new ForeignRegistrationLifecycleResponse(
                    Accepted: true,
                    Message: "stopped")),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        using var httpClient = CreateHttpClient(handler);
        var client = new ForeignRegistrationProtocolClient(httpClient, "run-token");

        var health = await client.HealthAsync();
        var initialized = await client.InitializeAsync(Manifest());
        var stopped = await client.ShutdownAsync(Manifest());

        health.ToRegistrationHealthStatus().IsHealthy.Should().BeTrue();
        initialized.Message.Should().Be("initialized");
        stopped.Message.Should().Be("stopped");
        handler.Requests.Select(r => r.Path).Should().Equal(
            ForeignRegistrationProtocol.HealthPath,
            ForeignRegistrationProtocol.InitializePath,
            ForeignRegistrationProtocol.ShutdownPath);
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
        var client = new ForeignRegistrationProtocolClient(httpClient, "run-token");

        var act = async () => await client.HealthAsync();

        var ex = await act.Should()
            .ThrowAsync<ForeignRegistrationProtocolException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ex.Which.ResponseBody.Should().Be("bad token");
    }

    private static PackageManifest Manifest() =>
        new(
            "sample_dotnet_registration",
            "Sample .NET Module",
            "1.0.0",
            "sdm",
            "SharpClaw.TestFixtures.ForeignSidecar.dll",
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
            request.Headers.TryGetValues(ForeignRegistrationProtocol.TokenHeaderName, out var tokens);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                tokens?.SingleOrDefault(),
                body));

            return responder(request, body);
        }
    }
}
