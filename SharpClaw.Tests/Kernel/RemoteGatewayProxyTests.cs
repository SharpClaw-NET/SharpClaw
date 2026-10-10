using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
[NonParallelizable]
internal sealed class RemoteGatewayProxyTests
{
    [TestCase("/fixture/echo", false), TestCase("/fixture/echo", true)]
    [TestCase("/api/moduleliteral", false), TestCase("/api/moduleliteral", true)]
    public async Task ProxyPreservesMethodQueryBodyAndStatusAcrossBothCredentialHopsAsync(string path, bool chunked)
    {
        var rig = await RemoteProxyTestRig.CreateAsync("peer-access").ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var client = rig.CreateClient();
        var bytes = Encoding.UTF8.GetBytes("alpha\nβ\nomega");
        using var content = new ByteArrayContent(bytes);
        using var request = new HttpRequestMessage(HttpMethod.Put, path + "?first=1&first=2&encoded=%2F") { Content = content };
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "local-caller-credential");
        request.Headers.Add("X-Gateway-Token", "local-gateway-credential");
        request.Headers.Add("Cookie", "local-session=must-not-cross");
        using var response = await client.SendAsync(request, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsByteArrayAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Equal(bytes);
        response.Headers.GetValues("X-Fixture").Should().Equal("remote-response");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        var gateway = rig.GatewayRequests.Should().ContainSingle().Which;
        gateway.Target.Should().Be("/api" + path + "?first=1&first=2&encoded=%2F");
        gateway.Authorization.Should().Be("Bearer peer-access");
        gateway.HasApiKey.Should().BeFalse();
        gateway.HasProxyHop.Should().BeTrue();
        gateway.HasGatewayToken.Should().BeFalse();
        gateway.HasCookie.Should().BeFalse();
        var runtime = rig.RuntimeRequests.Should().ContainSingle().Which;
        runtime.Method.Should().Be("PUT");
        runtime.Target.Should().Be(path + "?first=1&first=2&encoded=%2F");
        runtime.Body.Should().Equal(bytes);
        runtime.ApiKey.Should().Be("remote-runtime-key");
        runtime.GatewayToken.Should().Be("remote-gateway-token");
        rig.Actions.Should().Equal("security.api_key.resolve", "security.remote_pairing.validate",
            "runtime.request.receive", "runtime.request.handler.invoke");
    }

    [Test]
    public async Task MissingLocalKeyNeverReachesTheRemoteGatewayAsync()
    {
        var rig = await RemoteProxyTestRig.CreateAsync(null).ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var client = rig.CreateClient(authenticated: false);
        using var response = await client.GetAsync(new Uri("/fixture/echo", UriKind.Relative), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Locked);
        rig.GatewayRequests.Should().BeEmpty();
        rig.Actions.Should().Equal("security.api_key.resolve");
    }

    [TestCase("/env/core"), TestCase("/internal/storage"), TestCase("/configuration/options")]
    public async Task PrivateHostPathsCannotBeForwardedAsync(string path)
    {
        var rig = await RemoteProxyTestRig.CreateAsync(null).ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var client = rig.CreateClient();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        rig.GatewayRequests.Should().BeEmpty();
        rig.Actions.Should().Equal("security.api_key.resolve", "security.remote_pairing.validate");
    }

    [TestCase("//other.example/steal")]
    [TestCase("/fixture/%2fescape")]
    [TestCase("/fixture/%5cescape")]
    [TestCase("/fixture/%252fescape")]
    [TestCase("/fixture/../escape")]
    [TestCase("/fixture/%2e%2e/escape")]
    public async Task UnsafeRawTargetsFailBeforeAnyPeerTransportAsync(string rawTarget)
    {
        var rig = await RemoteProxyTestRig.CreateAsync(null).ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var proxy = new RemoteGatewayProxy(RemoteGatewayConnection.Create(rig.ProxyUri, null), CancellationToken.None);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/fixture/echo";
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        await proxy.ForwardAsync(context, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        rig.GatewayRequests.Should().BeEmpty();
    }

    [Test]
    public async Task AnExistingProxyHopCannotStartAnotherForwardAsync()
    {
        var rig = await RemoteProxyTestRig.CreateAsync(null).ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var client = rig.CreateClient();
        client.DefaultRequestHeaders.Add("X-SharpClaw-Proxy-Hop", "1");
        using var response = await client.GetAsync(new Uri("/fixture/echo", UriKind.Relative), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        rig.GatewayRequests.Should().BeEmpty();
    }

    [Test]
    public async Task RemoteFailureRemainsAFailureWithNoLocalHandlerResultAsync()
    {
        var rig = await RemoteProxyTestRig.CreateAsync(null).ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var client = rig.CreateClient();
        using var response = await client.GetAsync(new Uri("/fixture/fail", UriKind.Relative), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        rig.GatewayRequests.Should().ContainSingle();
        rig.RuntimeRequests.Should().BeEmpty();
        rig.LocalFallbackAttempts.Should().Be(0);
    }

    [Test]
    public async Task UnreachablePeerReturnsBadGatewayWithoutResolvingLocalRuntimeAsync()
    {
        var rig = await RemoteProxyTestRig.CreateAsync(null).ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        await rig.StopRemoteGatewayAsync().ConfigureAwait(false);
        using var client = rig.CreateClient();
        using var response = await client.GetAsync(new Uri("/fixture/echo", UriKind.Relative), TestContext.CurrentContext.CancellationToken)
            .ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        rig.RuntimeRequests.Should().BeEmpty();
        rig.LocalFallbackAttempts.Should().Be(0);
    }

    [Test]
    public async Task FirstStreamChunkArrivesWhileRemoteCompletionIsStillGatedAsync()
    {
        var rig = await RemoteProxyTestRig.CreateAsync(null).ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var client = rig.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fixture/stream");
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        // The nested finally cancels and directly joins this request before its
        // message/client/token owners are disposed, even if cancellation faults.
#pragma warning disable CA2025
        var responseWork = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, caller.Token);
#pragma warning restore CA2025
        try
        {
            await rig.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await responseWork.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            using var response = await responseWork.ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await using var streamDisposal = stream.ConfigureAwait(false);
            var first = new byte[13];
            using var chunkDeadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
            chunkDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            await stream.ReadExactlyAsync(first, chunkDeadline.Token).ConfigureAwait(false);
            Encoding.UTF8.GetString(first).Should().Be("data: first\n\n");
            rig.StreamSettled.Task.IsCompleted.Should().BeFalse();
            rig.StreamRelease.TrySetResult();
            using var remainder = new MemoryStream();
            await stream.CopyToAsync(remainder, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            Encoding.UTF8.GetString(remainder.ToArray()).Should().Be("data: last\n\n");
        }
        finally
        {
            rig.StreamRelease.TrySetResult();
            try { await caller.CancelAsync().ConfigureAwait(false); }
            finally
            {
                // Cancellation-callback failure cannot bypass the physical request join and response disposal.
                try { using var settledResponse = await responseWork.ConfigureAwait(false); }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            }
        }
    }

    [Test]
    public async Task CallerCancellationReachesTheRemoteRuntimeAndAllOwnedWorkSettlesAsync()
    {
        var rig = await RemoteProxyTestRig.CreateAsync(null).ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var client = rig.CreateClient();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        var operation = client.GetAsync(new Uri("/fixture/cancel", UriKind.Relative), caller.Token);
        try
        {
            await rig.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await caller.CancelAsync().ConfigureAwait(false);
            await rig.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            rig.CancelSettled.Task.IsCompleted.Should().BeFalse();
            // This test owns the request; the loopback pipeline has no UI/JTF dependency.
#pragma warning disable VSTHRD003
            Func<Task> observe = async () => { using var response = await operation.ConfigureAwait(false); };
#pragma warning restore VSTHRD003
            await observe.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
        }
        finally
        {
            rig.CancelRelease.TrySetResult();
            try { await caller.CancelAsync().ConfigureAwait(false); }
            finally
            {
                // Cancellation-callback failure cannot bypass the physical request join and response disposal.
                try { using var settledResponse = await operation.ConfigureAwait(false); }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            }
        }
        await rig.CancelSettled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
    }

    [Test]
    public async Task OwnedFrontendClientReturnsPeerRedirectWithoutFollowingItWithTheLocalKeyAsync()
    {
        var rig = await RemoteProxyTestRig.CreateAsync("peer-access").ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        var api = rig.CreateOwnedApiClient();
        await using var apiDisposal = api.ConfigureAwait(false);
        using var response = await api.GetAsync("/fixture/redirect", TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        rig.GatewayRequests.Should().ContainSingle();
        rig.RuntimeRequests.Should().BeEmpty("following the redirect would reach the echo endpoint");
    }

    [Test]
    public async Task WebSocketMessagesAndProtocolCrossTheAuthenticatedProxyChainAsync()
    {
        var rig = await RemoteProxyTestRig.CreateAsync("peer-access").ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Api-Key", rig.LocalKey);
        socket.Options.AddSubProtocol("fixture");
        var target = new UriBuilder(new Uri(rig.ProxyUri, "/fixture/socket")) { Scheme = "ws" }.Uri;
        await socket.ConnectAsync(target, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var sent = Encoding.UTF8.GetBytes("message");
        await socket.SendAsync(sent.AsMemory(), WebSocketMessageType.Text, true, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var received = new byte[32];
        var result = await socket.ReceiveAsync(received.AsMemory(), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        result.MessageType.Should().Be(WebSocketMessageType.Text);
        result.EndOfMessage.Should().BeTrue();
        received.AsSpan(0, result.Count).ToArray().Should().Equal(sent);
        socket.SubProtocol.Should().Be("fixture");
        var gateway = rig.GatewayRequests.Should().ContainSingle().Which;
        gateway.Authorization.Should().Be("Bearer peer-access");
        gateway.HasApiKey.Should().BeFalse();
        gateway.HasProxyHop.Should().BeTrue();
        var close = await socket.ReceiveAsync(received.AsMemory(), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        close.MessageType.Should().Be(WebSocketMessageType.Close);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        socket.CloseStatusDescription.Should().Be("complete");
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "complete", TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        await rig.SocketCloseCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        socket.State.Should().Be(WebSocketState.Closed);
    }

    [TestCase(401), TestCase(503)]
    public async Task FailedWebSocketHandshakeRetainsPeerStatusAndChallengeAsync(int status)
    {
        var rig = await RemoteProxyTestRig.CreateAsync("peer-access").ConfigureAwait(false);
        await using var rigDisposal = rig.ConfigureAwait(false);
        rig.SocketFailureStatus = status;
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("X-Api-Key", rig.LocalKey);
        var target = new UriBuilder(new Uri(rig.ProxyUri, "/fixture/socket")) { Scheme = "ws" }.Uri;
        Func<Task> connect = () => socket.ConnectAsync(target, TestContext.CurrentContext.CancellationToken);
        await connect.Should().ThrowAsync<WebSocketException>().ConfigureAwait(false);
        socket.HttpStatusCode.Should().Be((HttpStatusCode)status);
        socket.HttpResponseHeaders.Should().NotBeNull();
        socket.HttpResponseHeaders!["WWW-Authenticate"].Should().Equal("Bearer realm=\"fixture\"");
        rig.GatewayRequests.Should().ContainSingle();
        rig.RuntimeRequests.Should().BeEmpty();
    }

    [Test]
    public void FailedSecondSessionKeyWriteRemovesTheFirstOwnedKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpclaw-proxy-key-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, Path.Combine(root, "backend"), root);
            paths.EnsureDirectories();
            Directory.CreateDirectory(paths.GatewayTokenFilePath);
            Action create = () =>
            {
                using var keys = new ApiKeyProvider(paths);
            };
            var failure = create.Should().Throw<Exception>().Which;
            (failure is IOException or UnauthorizedAccessException).Should().BeTrue();
            File.Exists(paths.ApiKeyFilePath).Should().BeFalse();
            File.Exists(paths.DiscoveryEntryPath).Should().BeFalse();
            Directory.Exists(paths.GatewayTokenFilePath).Should().BeTrue("the fixture's blocking directory is borrowed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
