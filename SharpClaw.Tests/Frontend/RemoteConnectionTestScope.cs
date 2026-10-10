using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Frontend;

internal sealed class RemoteConnectionTestScope : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sharpclaw-remote-connect-" + Guid.NewGuid().ToString("N"));
    private readonly ModulePackageSources _sources = new();
    private readonly HttpClient _http;
    private int _backendStarts;

    internal RemoteConnectionTestScope()
    {
        Frontend = new FrontendInstanceService(Path.Combine(_root, "frontend"), _root, _root);
        BackendPaths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, Frontend.BundledBackendInstanceRoot, _root);
        BackendPaths.EnsureDirectories();
        File.WriteAllText(Path.Combine(BackendPaths.ConfigDirectory, ".env.template"), "Unrelated__Option=preserved\n");
        var executable = Path.Combine(_root, "runtime-host");
        File.WriteAllText(executable, string.Empty);
        Backend = new BackendProcessManager(LocalTarget, NullLogger<BackendProcessManager>.Instance,
            Frontend, executable, () => false, _ => Task.FromResult(false), _ => Interlocked.Increment(ref _backendStarts));
        Gateway = new GatewayProcessManager("http://127.0.0.1:48924", LocalTarget,
            NullLogger<GatewayProcessManager>.Instance, Frontend, executable,
            () => false, _ => Task.FromResult(false), _ => throw new AssertionException("The disabled Gateway must not launch."))
        { SkipLaunch = true };
        _http = CreateLocalHttpClient();
        Actions = new ClientActionDispatcher();
        Api = new SharpClawApiClient(_http, NullLogger<SharpClawApiClient>.Instance, Actions, "local-runtime-key");
        Modules = new ModulePackageStore(Path.Combine(_root, "packages"), _sources);
        Service = new RemoteBackendConnectionService(Frontend, Backend, Gateway, Api, Actions, Modules, CreatePeerHandler);
    }

    internal const string LocalTarget = "http://127.0.0.1:48923/";
    internal static Uri PeerAddress => new("https://peer.example/tenant");
    internal FrontendInstanceService Frontend { get; }
    internal SharpClawInstancePaths BackendPaths { get; }
    internal BackendProcessManager Backend { get; }
    internal GatewayProcessManager Gateway { get; }
    internal SharpClawApiClient Api { get; }
    internal ClientActionDispatcher Actions { get; }
    internal ModulePackageStore Modules { get; }
    internal RemoteBackendConnectionService Service { get; }
    internal int BackendStarts => Volatile.Read(ref _backendStarts);
    internal HttpStatusCode PeerStatus { get; set; } = HttpStatusCode.OK;
    internal bool BlockPeer { get; set; }
    internal bool LocalReady { get; set; } = true;
    internal bool CompatiblePeer { get; set; } = true;
    internal ConcurrentQueue<PeerRequest> PeerRequests { get; } = new();
    internal ConcurrentQueue<PeerHandler> PeerHandlers { get; } = new();
    internal TaskCompletionSource PeerEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource PeerCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource PeerRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string ProtectedFile => Path.Combine(BackendPaths.ConfigDirectory, ".env");

    internal Task SaveConnectionAsync(RemoteGatewayConnection? connection) =>
        new SupprocomSecretFileStore(LocalEnvironment.CreateSecretsOptions(BackendPaths.ConfigDirectory, false, BackendPaths))
            .UpdateDocumentAsync(settings => RemoteBackendConnectionService.UpdateSettings(settings, connection),
                TestContext.CurrentContext.CancellationToken);

    internal ConfigurationRoot ReadConfiguration() => (ConfigurationRoot)new ConfigurationBuilder()
        .AddLocalEnvironmentFrom(BackendPaths.ConfigDirectory, false, BackendPaths).Build();

    private HttpClient CreateLocalHttpClient()
    {
        // The returned HttpClient owns this handler; construction failure disposes the handler or its owning client.
#pragma warning disable CA2000
        var handler = new LocalHandler(this);
#pragma warning restore CA2000
        HttpClient? http = null;
        try
        {
            http = new HttpClient(handler, disposeHandler: true);
            http.BaseAddress = new Uri(LocalTarget);
            return http;
        }
        catch
        {
            if (http is null) handler.Dispose();
            else http.Dispose();
            throw;
        }
    }

    private PeerHandler CreatePeerHandler()
    {
        var handler = new PeerHandler(this);
        PeerHandlers.Enqueue(handler);
        return handler;
    }

    public async ValueTask DisposeAsync()
    {
        PeerRelease.TrySetResult();
        await Service.DisposeAsync().ConfigureAwait(false);
        await Api.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        Modules.Dispose();
        _sources.Dispose();
        Gateway.Dispose();
        Backend.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    internal sealed record PeerRequest(Uri Target, string? Authorization, bool HasLocalKey, bool HasGatewayToken, bool HasCookie);

    internal sealed class PeerHandler(RemoteConnectionTestScope scope) : HttpMessageHandler
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            scope.PeerRequests.Enqueue(new PeerRequest(request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Headers.Contains("X-Api-Key"), request.Headers.Contains("X-Gateway-Token"), request.Headers.Contains("Cookie")));
            scope.PeerEntered.TrySetResult();
            if (scope.BlockPeer)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    scope.PeerCancelled.TrySetResult();
                    await scope.PeerRelease.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            var json = request.RequestUri!.AbsolutePath.EndsWith("gateway/status", StringComparison.Ordinal)
                ? "{\"status\":\"ready\",\"runtime\":\"https://remote-runtime.example/\"}"
                : request.RequestUri.AbsolutePath.EndsWith("health", StringComparison.Ordinal)
                    ? "{\"status\":\"healthy\"}" : "{\"status\":\"authenticated\"}";
            if (!scope.CompatiblePeer) json = "{\"status\":\"healthy\",\"runtime\":\"file:///private\"}";
            return new HttpResponseMessage(scope.PeerStatus) { Content = new StringContent(json) };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref _disposals);
            base.Dispose(disposing);
        }
    }

    private sealed class LocalHandler(RemoteConnectionTestScope scope) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = !scope.LocalReady && string.Equals(request.RequestUri!.AbsolutePath, "/readyz", StringComparison.Ordinal)
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            var json = string.Equals(request.RequestUri!.AbsolutePath, "/remote/status", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new
                {
                    mode = "gateway-proxy",
                    gatewayUrl = RemoteGatewayConnection.Create(PeerAddress, null).GatewayBaseUri.AbsoluteUri,
                    connected = true,
                }) : "{}";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }
}
