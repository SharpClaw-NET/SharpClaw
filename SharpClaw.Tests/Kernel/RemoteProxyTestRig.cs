using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Gateway;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

internal sealed class RemoteProxyTestRig : IAsyncDisposable
{
    private const string RuntimeKey = "remote-runtime-key";
    private const string GatewayToken = "remote-gateway-token";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sharpclaw-remote-proxy-" + Guid.NewGuid().ToString("N"));
    private readonly string? _peerToken;
    private WebApplication? _runtime;
    private WebApplication? _gateway;
    private WebApplication? _proxy;
    private SharpClawInstancePaths? _proxyPaths;
    private int _localFallbackAttempts;

    private RemoteProxyTestRig(string? peerToken) => _peerToken = peerToken;

    internal ConcurrentQueue<GatewayRequest> GatewayRequests { get; } = new();
    internal ConcurrentQueue<RuntimeRequest> RuntimeRequests { get; } = new();
    internal ConcurrentQueue<string> Actions { get; } = new();
    internal int? SocketFailureStatus { get; set; }
    internal TaskCompletionSource StreamEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource StreamRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource StreamSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CancelEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CancelRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CancelSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource SocketCloseCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Uri ProxyUri => Address(_proxy!);
    internal string LocalKey => _proxy!.Services.GetRequiredService<ApiKeyProvider>().ApiKey;
    internal int LocalFallbackAttempts => Volatile.Read(ref _localFallbackAttempts);

    internal Task StopRemoteGatewayAsync() => _gateway!.StopAsync(TestContext.CurrentContext.CancellationToken);

    internal static async Task<RemoteProxyTestRig> CreateAsync(string? peerToken)
    {
        var rig = new RemoteProxyTestRig(peerToken);
        try
        {
            rig._runtime = rig.CreateRuntime();
            await rig._runtime.StartAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            rig._gateway = rig.CreateGateway();
            await rig._gateway.StartAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            rig._proxy = rig.CreateProxy();
            await rig._proxy.StartAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            _ = rig.LocalKey;
            rig._proxyPaths!.PublishDiscoveryEntry(rig.ProxyUri.ToString());
            return rig;
        }
        catch
        {
            await rig.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal HttpClient CreateClient(bool authenticated = true)
    {
        // The returned HttpClient owns this handler; construction failure disposes the handler or its owning client.
#pragma warning disable CA2000
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
#pragma warning restore CA2000
        HttpClient? client = null;
        try
        {
            client = new HttpClient(handler, disposeHandler: true);
            client.BaseAddress = ProxyUri;
            client.Timeout = Timeout.InfiniteTimeSpan;
            if (authenticated) client.DefaultRequestHeaders.Add("X-Api-Key", LocalKey);
            return client;
        }
        catch
        {
            if (client is null) handler.Dispose();
            else client.Dispose();
            throw;
        }
    }

    internal SharpClawApiClient CreateOwnedApiClient()
    {
        var frontend = new FrontendInstanceService(Path.Combine(_root, "frontend"), _root, _root);
        return new SharpClawApiClient(ProxyUri.ToString(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SharpClawApiClient>.Instance,
            frontend, new ClientActionDispatcher());
    }

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder;
    }

    private WebApplication CreateRuntime()
    {
        var app = Builder().Build();
        app.UseWebSockets();
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (!string.Equals(context.Request.Headers["X-Api-Key"], RuntimeKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status423Locked;
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        app.Map("/fixture/echo", EchoAsync);
        app.Map("/api/moduleliteral", EchoAsync);
        app.Map("/fixture/stream", StreamAsync);
        app.Map("/fixture/cancel", CancelAsync);
        app.Map("/fixture/socket", SocketAsync);
        app.MapGet("/fixture/fail", () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/fixture/redirect", () => Results.Redirect(new Uri(Address(_gateway!), "api/fixture/echo").ToString()));
        app.MapGet("/readyz", () => Results.Ok());
        app.MapGet("/ping", () => Results.Ok());
        return app;
    }

    private WebApplication CreateGateway()
    {
        var builder = Builder();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(Options.Create(new InternalApiOptions
        {
            BaseUrl = Address(_runtime!).ToString(), ApiKey = RuntimeKey, GatewayToken = GatewayToken,
        }));
        builder.Services.AddHttpClient<InternalApiClient>(http => http.BaseAddress = Address(_runtime!))
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            GatewayRequests.Enqueue(new GatewayRequest(context.Request.Path.Value + context.Request.QueryString.Value,
                context.Request.Headers.Authorization.ToString(), context.Request.Headers.ContainsKey("X-Api-Key"),
                context.Request.Headers.ContainsKey("X-Gateway-Token"), context.Request.Headers.ContainsKey("Cookie"),
                context.Request.Headers.ContainsKey("X-SharpClaw-Proxy-Hop")));
            if (SocketFailureStatus is { } socketStatus && context.Request.Path == "/api/fixture/socket")
            {
                context.Response.StatusCode = socketStatus;
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"fixture\"";
                return;
            }
            var expected = _peerToken is null ? string.Empty : "Bearer " + _peerToken;
            if (!string.Equals(context.Request.Headers.Authorization.ToString(), expected, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        app.MapGatewayProxyEndpoints();
        return app;
    }

    private WebApplication CreateProxy()
    {
        var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, Path.Combine(_root, "backend"), _root);
        _proxyPaths = paths;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["Database:Provider"] = "unavailable-local-persistence",
        }).Build();
        return RemoteProxyHost.BuildApplication([], configuration, paths,
            RemoteGatewayConnection.Create(Address(_gateway!), _peerToken), services =>
            {
                services.AddLogging(logging => logging.ClearProviders());
                services.AddSingleton(this);
                services.AddSingleton<RuntimeKernelAdapter>(_ =>
                {
                    Interlocked.Increment(ref _localFallbackAttempts);
                    throw new AssertionException("Remote proxy mode must not resolve an ordinary local Runtime adapter.");
                });
                services.AddScoped(static provider => new ActionObserver(provider.GetRequiredService<RemoteProxyTestRig>()));
                foreach (var key in ObservedActions())
                    services.AddSingleton(new ActionHookBinding("remote-test", BehaviorTargetKind.Exact, key,
                        null, typeof(ActionObserver), false, new HookOrdering("observe-" + key.Value),
                        typeof(ActionObserver).AssemblyQualifiedName!));
                services.AddSingleton(static provider => CreateObservedGraph(provider));
            });
    }

    private static SharpClawActionKey[] ObservedActions() =>
    [new("runtime.request.receive"), new("runtime.request.handler.invoke"),
     new("security.api_key.resolve"), new("security.remote_pairing.validate")];

    private static KernelGraph CreateObservedGraph(IServiceProvider provider)
    {
        var actions = ObservedActions();
        var grants = actions.ToDictionary(static key => key.Value,
            static _ => ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap, StringComparer.Ordinal);
        var approvals = actions.Where(static key => KernelActionCatalog.DescriptorFor(key).ContainsSensitiveData)
            .Select(static key =>
            {
                var descriptor = KernelActionCatalog.DescriptorFor(key).ToDescriptor();
                var types = KernelSchemaIdentity.ActionTypes(descriptor, typeof(KernelActionEnvelope), typeof(object));
                return new KernelSensitiveActionApproval("remote-test", key, descriptor.Version,
                    types.ActionType.AssemblyQualifiedName!, types.ResultType.AssemblyQualifiedName!, KernelSchemaIdentity.Action(descriptor));
            }).ToArray();
        return new KernelGraphBuilder().Compile(provider, new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
            { ["remote-test"] = grants },
            SensitiveActionApprovals = approvals,
        });
    }

    private async Task EchoAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted).ConfigureAwait(false);
        var bytes = body.ToArray();
        RuntimeRequests.Enqueue(new RuntimeRequest(context.Request.Method,
            context.Request.Path.Value + context.Request.QueryString.Value, bytes,
            context.Request.Headers["X-Api-Key"].ToString(), context.Request.Headers["X-Gateway-Token"].ToString(),
            context.Request.Headers.Authorization.ToString()));
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers["X-Fixture"] = "remote-response";
        context.Response.Headers.SetCookie = "remote-cookie=must-not-cross";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task StreamAsync(HttpContext context)
    {
        try
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n", context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            StreamEntered.TrySetResult();
            await StreamRelease.Task.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsync("data: last\n\n", context.RequestAborted).ConfigureAwait(false);
        }
        finally { StreamSettled.TrySetResult(); }
    }

    private async Task CancelAsync(HttpContext context)
    {
        try
        {
            CancelEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                CancelObserved.TrySetResult();
                await CancelRelease.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { CancelSettled.TrySetResult(); }
    }

    private async Task SocketAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync("fixture").ConfigureAwait(false);
        var bytes = new byte[32];
        var received = await socket.ReceiveAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        await socket.SendAsync(bytes.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage,
            context.RequestAborted).ConfigureAwait(false);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", context.RequestAborted).ConfigureAwait(false);
        SocketCloseCompleted.TrySetResult();
    }

    private static Uri Address(WebApplication app) => new(app.Urls.Should().ContainSingle().Which);

    public async ValueTask DisposeAsync()
    {
        StreamRelease.TrySetResult();
        CancelRelease.TrySetResult();
        foreach (var app in new[] { _proxy, _gateway, _runtime })
        {
            if (app is null) continue;
            try { await app.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { await app.DisposeAsync().ConfigureAwait(false); }
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    internal sealed record GatewayRequest(string Target, string Authorization, bool HasApiKey, bool HasGatewayToken, bool HasCookie, bool HasProxyHop);
    internal sealed record RuntimeRequest(string Method, string Target, byte[] Body, string ApiKey, string GatewayToken, string Authorization);

    private sealed class ActionObserver(RemoteProxyTestRig rig) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken)
        {
            rig.Actions.Enqueue(context.ActionKey.Value);
            return control.ProceedAsync(cancellationToken);
        }
    }
}
