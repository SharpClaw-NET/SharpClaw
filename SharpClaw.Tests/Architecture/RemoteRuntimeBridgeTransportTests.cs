using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;
using Yarp.ReverseProxy.Forwarder;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimeBridgeTransportTests
{
    [Test]
    public async Task Proxy_normalizes_forwarder_service_unavailable_without_rewriting_application_errors()
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Body = new MemoryStream();
        context.Features.Set<IForwarderErrorFeature>(new ForwarderErrorFeatureStub());

        await RemoteProxyHost.NormalizeForwarderErrorAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.ContentType.Should().StartWith("application/json");
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        (await reader.ReadToEndAsync()).Should().Contain("ProxyServiceUnavailable");
    }

    [Test]
    public async Task Proxy_transformer_removes_local_and_authoritative_credentials_before_gateway_forwarding()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "local-proxy-api-key";
        context.Request.Headers["X-Gateway-Token"] = "authoritative-api-key";
        context.Request.Headers["X-SharpClaw-Bridge-Authoritative-Key"] = "authoritative-api-key";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://gateway.example.test/api/credential-boundary");
        var transformer = new RemoteProxyHost.RemoteProxyTransformer("proxy-1");

        await transformer.TransformRequestAsync(
            context,
            request,
            "https://gateway.example.test",
            CancellationToken.None);

        request.Headers.Contains("X-Api-Key").Should().BeFalse();
        request.Headers.Contains("X-Gateway-Token").Should().BeFalse();
        request.Headers.Should().NotContainKey("X-SharpClaw-Bridge-Authoritative-Key");
        request.Headers.GetValues(RemoteRuntimeBridgePaths.ProxyIdentityHeader)
            .Should().ContainSingle("proxy-1");
    }

    [Test]
    public async Task Forwarder_failure_has_stable_transport_error_and_application_5xx_is_preserved()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "bridge-forwarder-errors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var serverCertificatePath = Path.Combine(root, "bridge.pfx");
        using var serverCertificate = CreateServerCertificate();
        File.WriteAllBytes(
            serverCertificatePath,
            serverCertificate.Export(X509ContentType.Pfx));

        var unreachablePort = GetFreePort();
        var target = new RemoteRuntimeBridgeTarget(
            "gateway-1",
            "runtime-1",
            "runtime-install-1",
            $"http://127.0.0.1:{unreachablePort}",
            "authoritative-api-key",
            "authoritative-gateway-token");
        await using var registryClient = new InMemoryRemoteRuntimePairingRegistryClient(
            target,
            active: true);
        using var clientCertificate = registryClient.ClientCertificate;
        await using var app = await RemoteRuntimeBridgeHost.BuildAsync(
            [],
            new RemoteRuntimeBridgeOptions
            {
                Enabled = true,
                ListenUrl = $"https://127.0.0.1:{GetFreePort()}",
                ServerCertificatePath = serverCertificatePath,
            },
            registryClient,
            target);

        try
        {
            await app.StartAsync();
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                    presented is not null
                    && string.Equals(
                        RemoteRuntimeCertificateHash.Compute(presented),
                        RemoteRuntimeCertificateHash.Compute(serverCertificate),
                        StringComparison.Ordinal),
            };
            handler.ClientCertificates.Add(clientCertificate);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://127.0.0.1:{new Uri(address).Port}"),
            };
            client.DefaultRequestHeaders.Add(
                RemoteRuntimeBridgePaths.ProxyIdentityHeader,
                "proxy-1");

            using var response = await client.GetAsync("/api/unavailable");
            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            (await response.Content.ReadAsStringAsync())
                .Should().Contain("BridgeBadGateway");
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Both_yarp_hops_preserve_http_stream_upload_range_websocket_and_cancellation()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "bridge-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var serverCertificatePath = Path.Combine(root, "bridge.pfx");
        using var serverCertificate = CreateServerCertificate();
        File.WriteAllBytes(
            serverCertificatePath,
            serverCertificate.Export(X509ContentType.Pfx));

        var upstreamUrl = $"http://127.0.0.1:{GetFreePort()}";
        await using var upstream = await UpstreamHarness.StartAsync(upstreamUrl);

        var target = new RemoteRuntimeBridgeTarget(
            "gateway-1",
            "runtime-1",
            "runtime-install-1",
            upstreamUrl,
            "authoritative-api-key",
            "authoritative-gateway-token");
        await using var registryClient = new InMemoryRemoteRuntimePairingRegistryClient(
            target,
            active: true);
        using var clientCertificate = registryClient.ClientCertificate;
        var gatewayOptions = new RemoteRuntimeBridgeOptions
        {
            Enabled = true,
            ListenUrl = $"https://127.0.0.1:{GetFreePort()}",
            ServerCertificatePath = serverCertificatePath,
        };
        await using var gateway = await RemoteRuntimeBridgeHost.BuildAsync(
            [],
            gatewayOptions,
            registryClient,
            target);

        var rootPaths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            Path.Combine(root, "backend"),
            Path.Combine(root, "shared"));
        var localUrl = $"http://127.0.0.1:{GetFreePort()}";
        RemoteRuntimeProxyConnection? connection = null;
        WebApplication? proxy = null;
        try
        {
            await gateway.StartAsync();
            connection = RemoteRuntimeProxyConnection.Create(
                rootPaths,
                localUrl,
                gatewayOptions.ListenUrl,
                RemoteRuntimeCertificateHash.Compute(serverCertificate),
                "proxy-1",
                clientCertificate,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30));
            proxy = RemoteProxyHost.Build([], connection);
            await proxy.StartAsync();

            using var client = new HttpClient
            {
                BaseAddress = new Uri(localUrl),
                Timeout = TimeSpan.FromSeconds(10),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            client.DefaultRequestHeaders.Add("X-Api-Key", connection.LocalApiKey);
            client.DefaultRequestHeaders.Add("X-Test", "preserved");

            using var headersResponse = await client.GetAsync("/api/headers");
            headersResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var headersBody = await headersResponse.Content.ReadAsStringAsync();
            headersBody.Should().Be(
                "GET|/api/headers|preserved|authoritative-api-key|");
            (await registryClient.FindAsync(
                registryClient.PairId,
                CancellationToken.None))!
                .LastSeenAtUtc.Should().NotBeNull();

            using var cliLikeResponse = await client.GetAsync("/api/cli-like");
            cliLikeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await cliLikeResponse.Content.ReadAsStringAsync())
                .Should().Be("GET|/api/cli-like|preserved|authoritative-api-key|");

            using var applicationErrorResponse = await client.GetAsync("/api/application-error");
            applicationErrorResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            (await applicationErrorResponse.Content.ReadAsStringAsync())
                .Should().Be("application-error");

            var uploadPayload = Enumerable.Range(0, 128)
                .Select(static value => (byte)value)
                .ToArray();
            using var uploadResponse = await client.PostAsync(
                "/api/upload",
                new ByteArrayContent(uploadPayload));
            uploadResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await uploadResponse.Content.ReadAsByteArrayAsync())
                .Should().Equal(uploadPayload);

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/range");
            rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
            using var rangeResponse = await client.SendAsync(rangeRequest);
            rangeResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            rangeResponse.Content.Headers.ContentRange!.From.Should().Be(2);
            rangeResponse.Content.Headers.ContentRange.To.Should().Be(5);
            (await rangeResponse.Content.ReadAsStringAsync()).Should().Be("2345");

            using var streamResponse = await client.GetAsync(
                "/api/stream",
                HttpCompletionOption.ResponseHeadersRead);
            streamResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            streamResponse.Content.Headers.ContentType!.MediaType
                .Should().Be("text/event-stream");
            using var streamReader = new StreamReader(
                await streamResponse.Content.ReadAsStreamAsync());
            (await streamReader.ReadToEndAsync()).Should().Be("one\ntwo\nthree\n");

            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-Api-Key", connection.LocalApiKey);
            var webSocketUri = new Uri(
                localUrl.Replace("http://", "ws://", StringComparison.Ordinal)
                + "/ws");
            await socket.ConnectAsync(webSocketUri, CancellationToken.None);
            var message = Encoding.UTF8.GetBytes("hello");
            await socket.SendAsync(
                message,
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
            var responseBuffer = new byte[64];
            var received = await socket.ReceiveAsync(
                responseBuffer,
                CancellationToken.None);
            received.MessageType.Should().Be(WebSocketMessageType.Text);
            Encoding.UTF8.GetString(responseBuffer, 0, received.Count)
                .Should().Be("echo:hello");
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "test complete",
                CancellationToken.None);

            using var cliSocket = new ClientWebSocket();
            cliSocket.Options.SetRequestHeader("X-Api-Key", connection.LocalApiKey);
            var cliUri = new Uri(
                localUrl.Replace("http://", "ws://", StringComparison.Ordinal)
                + RemoteRuntimeBridgePaths.CliControl);
            await cliSocket.ConnectAsync(cliUri, CancellationToken.None);
            var cliCommand = JsonSerializer.SerializeToUtf8Bytes(
                RemoteRuntimeCliFrame.CommandFrame(["help"]));
            await cliSocket.SendAsync(
                cliCommand,
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);

            var cliOutput = await ReceiveCliFrameAsync(cliSocket);
            cliOutput.Type.Should().Be(RemoteRuntimeCliFrameTypes.Output);
            cliOutput.Text.Should().Contain("command=help");
            cliOutput.Text.Should().Contain("api=authoritative-api-key");
            cliOutput.Text.Should().Contain("gateway=authoritative-gateway-token");
            var cliResult = await ReceiveCliFrameAsync(cliSocket);
            cliResult.Type.Should().Be(RemoteRuntimeCliFrameTypes.Exit);
            cliResult.Handled.Should().BeTrue();
            await cliSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "test complete",
                CancellationToken.None);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var cancellationResponse = await client.GetAsync(
                "/api/cancel",
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token);
            var cancellationStream = await cancellationResponse.Content.ReadAsStreamAsync(
                cancellation.Token);
            var firstChunk = new byte[5];
            (await cancellationStream.ReadAsync(firstChunk, cancellation.Token))
                .Should().Be(5);
            cancellation.Cancel();
            cancellationResponse.Dispose();
            await upstream.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (proxy is not null)
                await proxy.StopAsync();
            connection?.Dispose();
            await gateway.StopAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=bridge-transport-test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }


    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<RemoteRuntimeCliFrame> ReceiveCliFrameAsync(
        ClientWebSocket socket)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            await message.WriteAsync(buffer.AsMemory(0, result.Count));
        }
        while (!result.EndOfMessage);

        return JsonSerializer.Deserialize<RemoteRuntimeCliFrame>(
            message.GetBuffer().AsSpan(0, checked((int)message.Length)))!;
    }

    private sealed class ForwarderErrorFeatureStub : IForwarderErrorFeature
    {
        public ForwarderError Error => ForwarderError.Request;

        public Exception? Exception => new InvalidOperationException("test forwarder failure");
    }

    private sealed class UpstreamHarness : IAsyncDisposable
    {
        private UpstreamHarness(
            WebApplication app,
            TaskCompletionSource<bool> aborted)
        {
            App = app;
            Aborted = aborted;
        }

        public WebApplication App { get; }

        public TaskCompletionSource<bool> Aborted { get; }

        public static async Task<UpstreamHarness> StartAsync(string url)
        {
            var aborted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = WebApplication.CreateSlimBuilder([]);
            builder.WebHost.UseUrls(url);
            var app = builder.Build();
            app.UseWebSockets();
            app.Run(context => HandleAsync(context, aborted));
            await app.StartAsync();
            return new UpstreamHarness(app, aborted);
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();

        private static async Task HandleAsync(
            HttpContext context,
            TaskCompletionSource<bool> aborted)
        {
            if (context.Request.Path == RemoteRuntimeBridgePaths.CliControl)
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[8 * 1024];
                var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                var command = JsonSerializer.Deserialize<RemoteRuntimeCliFrame>(
                    buffer.AsSpan(0, received.Count))?.Arguments;
                var output = JsonSerializer.SerializeToUtf8Bytes(
                    new RemoteRuntimeCliFrame(
                        RemoteRuntimeCliFrameTypes.Output,
                        Text: $"command={string.Join(' ', command ?? [])};api={context.Request.Headers["X-Api-Key"]};gateway={context.Request.Headers["X-Gateway-Token"]}"));
                await socket.SendAsync(
                    output,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    context.RequestAborted);
                var result = JsonSerializer.SerializeToUtf8Bytes(
                    RemoteRuntimeCliFrame.ExitFrame(0, handled: true));
                await socket.SendAsync(
                    result,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    context.RequestAborted);

                while (true)
                {
                    var close = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (close.MessageType != WebSocketMessageType.Close)
                        continue;

                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "test complete",
                        context.RequestAborted);
                    break;
                }

                return;
            }

            if (context.Request.Path == "/ws")
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[256];
                var received = await socket.ReceiveAsync(
                    buffer,
                    context.RequestAborted);
                var response = Encoding.UTF8.GetBytes(
                    "echo:" + Encoding.UTF8.GetString(buffer, 0, received.Count));
                await socket.SendAsync(
                    response,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    context.RequestAborted);

                while (true)
                {
                    var close = await socket.ReceiveAsync(
                        buffer,
                        context.RequestAborted);
                    if (close.MessageType != WebSocketMessageType.Close)
                        continue;

                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "test complete",
                        context.RequestAborted);
                    break;
                }

                return;
            }

            if (context.Request.Path == "/api/headers"
                || context.Request.Path == "/api/cli-like")
            {
                await context.Response.WriteAsync(
                    string.Join(
                        "|",
                        context.Request.Method,
                        context.Request.Path,
                        context.Request.Headers["X-Test"].ToString(),
                        context.Request.Headers["X-Api-Key"].ToString(),
                        context.Request.Headers["X-Gateway-Token"].ToString()),
                    context.RequestAborted);
                return;
            }

            if (context.Request.Path == "/api/application-error")
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("application-error", context.RequestAborted);
                return;
            }

            if (context.Request.Path == "/api/upload")
            {
                await context.Request.Body.CopyToAsync(
                    context.Response.Body,
                    context.RequestAborted);
                return;
            }

            if (context.Request.Path == "/api/range")
            {
                var range = context.Request.Headers.Range.ToString();
                if (!string.Equals(range, "bytes=2-5", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength = 4;
                context.Response.Headers.ContentRange = "bytes 2-5/10";
                await context.Response.WriteAsync("2345", context.RequestAborted);
                return;
            }

            if (context.Request.Path == "/api/stream")
            {
                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                context.Response.ContentType = "text/event-stream";
                foreach (var item in new[] { "one\n", "two\n", "three\n" })
                {
                    await context.Response.WriteAsync(item, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await Task.Delay(10, context.RequestAborted);
                }

                return;
            }

            if (context.Request.Path == "/api/cancel")
            {
                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                try
                {
                    await context.Response.WriteAsync(
                        new string('s', 8192),
                        context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    aborted.TrySetResult(true);
                }

                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }
}
