using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.Host;

internal sealed class RemoteGatewayProxy : IDisposable
{
    private readonly RemoteGatewayConnection _connection;
    private readonly CancellationToken _stoppingToken;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _client;

    public RemoteGatewayProxy(RemoteGatewayConnection connection, CancellationToken stoppingToken)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _stoppingToken = stoppingToken;
        _handler = new SocketsHttpHandler();
        try
        {
            _handler.AllowAutoRedirect = false;
            _handler.UseCookies = false;
            _handler.AutomaticDecompression = DecompressionMethods.None;
            _handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;
            _client = new HttpClient(_handler, disposeHandler: false);
            _client.Timeout = Timeout.InfiniteTimeSpan;
        }
        catch
        {
            try
            {
                _client?.Dispose();
            }
            finally
            {
                _handler.Dispose();
            }
            throw;
        }
    }

    internal async ValueTask ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!RemoteProxyRoutePolicy.IsAllowed(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, context.RequestAborted, _stoppingToken);
        var target = CreateTarget(context.Request);
        if (context.WebSockets.IsWebSocketRequest)
            await ForwardWebSocketAsync(context, target, cancellation.Token).ConfigureAwait(false);
        else
            await ForwardHttpAsync(context, target, cancellation.Token).ConfigureAwait(false);
    }

    internal async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_connection.GatewayBaseUri, "readyz"));
        AttachPeerCredential(request);
        try
        {
            using var response = await _client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private Uri CreateTarget(HttpRequest request)
    {
        var relative = RemoteProxyRoutePolicy.GetRelativePath(request.Path.ToUriComponent());
        var target = new Uri(_connection.GatewayBaseUri, "./" + relative + request.QueryString.ToUriComponent());
        var gateway = _connection.GatewayBaseUri;
        if (!target.Scheme.Equals(gateway.Scheme, StringComparison.Ordinal)
            || !target.Host.Equals(gateway.Host, StringComparison.Ordinal) || target.Port != gateway.Port
            || !target.AbsolutePath.StartsWith(gateway.AbsolutePath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(target.Fragment))
            throw new InvalidOperationException("The proxy request escaped the configured Gateway API prefix.");
        return target;
    }

    private async Task ForwardHttpAsync(HttpContext context, Uri target, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (CanHaveBody(context))
            request.Content = new RemoteProxyRequestContent(context.Request);
        RemoteProxyHeaders.CopyRequest(context.Request, request);
        AttachPeerCredential(request);
        using var response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        context.Response.StatusCode = (int)response.StatusCode;
        RemoteProxyHeaders.CopyResponse(response, context.Response);
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bodyAsyncDisposal = body.ConfigureAwait(false);
        await body.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool CanHaveBody(HttpContext context) =>
        context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
        || context.Request.ContentLength.HasValue
        || context.Request.Headers.ContainsKey("Transfer-Encoding")
        || context.Request.Headers.ContainsKey("Content-Type");

    private void AttachPeerCredential(HttpRequestMessage request)
    {
        request.Headers.Add(RemoteProxyRoutePolicy.HopHeader, "1");
        if (_connection.AccessToken is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task ForwardWebSocketAsync(HttpContext context, Uri target, CancellationToken cancellationToken)
    {
        using var upstream = new ClientWebSocket();
        ConfigureWebSocket(context, upstream);
        var webSocketTarget = new UriBuilder(target)
        {
            Scheme = target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal) ? "wss" : "ws",
        }.Uri;
        try
        {
            await upstream.ConnectAsync(webSocketTarget, _client, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException) when (upstream.HttpStatusCode != default && upstream.HttpStatusCode != HttpStatusCode.SwitchingProtocols)
        {
            CopyUpgradeRejection(upstream, context.Response);
            return;
        }
        using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol).ConfigureAwait(false);
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await JoinWebSocketRelaysAsync(downstream, upstream, relayCancellation, cancellationToken).ConfigureAwait(false);
    }

    private void ConfigureWebSocket(HttpContext context, ClientWebSocket upstream)
    {
        upstream.Options.CollectHttpResponseDetails = true;
        var tokens = RemoteProxyHeaders.ConnectionTokens(context.Request.Headers.Connection);
        foreach (var header in context.Request.Headers)
        {
            if (RemoteProxyHeaders.IsAllowed(header.Key, tokens)
                && !header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                upstream.Options.SetRequestHeader(header.Key, header.Value.ToString());
        }
        upstream.Options.SetRequestHeader(RemoteProxyRoutePolicy.HopHeader, "1");
        if (_connection.AccessToken is { } token)
            upstream.Options.SetRequestHeader("Authorization", "Bearer " + token);
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
            upstream.Options.AddSubProtocol(protocol);
    }

    private static void CopyUpgradeRejection(ClientWebSocket upstream, HttpResponse response)
    {
        response.StatusCode = (int)upstream.HttpStatusCode;
        if (upstream.HttpResponseHeaders is not { } headers)
            return;
        var tokens = RemoteProxyHeaders.ConnectionTokens(headers.TryGetValue("Connection", out var connection) ? connection : []);
        RemoteProxyHeaders.CopyResponseHeaders(headers, response.Headers, tokens);
    }

    private static async Task JoinWebSocketRelaysAsync(
        WebSocket downstream,
        WebSocket upstream,
        CancellationTokenSource relayCancellation,
        CancellationToken cancellationToken)
    {
        var toGateway = RelayWebSocketAsync(downstream, upstream, relayCancellation.Token);
        var toClient = RelayWebSocketAsync(upstream, downstream, relayCancellation.Token);
        var relays = Task.WhenAll(toGateway, toClient);
        ExceptionDispatchInfo? failure = null;
        try
        {
            var first = await Task.WhenAny(toGateway, toClient).ConfigureAwait(false);
#pragma warning disable VSTHRD003 // Both context-free socket relays were started above and are retained in relays; the finally block joins them before either socket is disposed.
            await first.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            try
            {
                await relays.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!relays.IsFaulted)
            {
                // The close frame was forwarded; an absent peer acknowledgement is bounded before aborting the remaining receive.
            }
        }
#pragma warning disable CA1031 // Retain the first relay/caller failure, cancel and join both physical relays, then rethrow it below.
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031
        finally
        {
            var cancellation = relayCancellation.CancelAsync();
            try
            {
                await Task.WhenAll(relays, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (relayCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // Observe cancellation callback and secondary relay failures without replacing the first failure; all started work has settled here.
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
#pragma warning restore CA1031
        }
        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task RelayWebSocketAsync(
        WebSocket source,
        WebSocket destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await destination.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription, cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType,
                result.EndOfMessage, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        try
        {
            _client.Dispose();
        }
        finally
        {
            _handler.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
