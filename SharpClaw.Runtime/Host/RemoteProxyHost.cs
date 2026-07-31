using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;

namespace SharpClaw.Runtime.Host;

public static class RemoteProxyHost
{
    public static Task RunAsync(
        RuntimeLaunchPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Mode != RuntimeLaunchMode.RemoteProxy)
            throw new ArgumentException("The launch plan is not RemoteProxy mode.", nameof(plan));

        cancellationToken.ThrowIfCancellationRequested();
        RemoteRuntimePairingAuthorization.RequireApprovedPair(plan.PairingFile);

        throw new NotSupportedException(
            "RemoteProxy mode has no transport host configured. Pairing and transport setup are required before binding.");
    }

    public static WebApplication Build(string[] args, RuntimeLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mode != RuntimeLaunchMode.RemoteProxy)
            throw new ArgumentException("The launch plan is not RemoteProxy mode.", nameof(plan));

        RemoteRuntimePairingAuthorization.RequireApprovedPair(plan.PairingFile);
        if (string.IsNullOrWhiteSpace(plan.GatewayBridgeUrl))
        {
            throw new InvalidOperationException(
                "RemoteProxy mode requires a fixed Gateway bridge URL before binding.");
        }

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls(plan.LocalUrl ?? "http://127.0.0.1:48923");
        builder.Services.AddReverseProxy();

        var app = builder.Build();
        app.MapForwarder(
            "/{**catch-all}",
            plan.GatewayBridgeUrl,
            ForwarderRequestConfig.Empty,
            new RemoteProxyTransformer());
        return app;
    }

    private sealed class RemoteProxyTransformer : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(
                httpContext,
                proxyRequest,
                destinationPrefix,
                cancellationToken);
            proxyRequest.Headers.Remove("X-Api-Key");
            proxyRequest.Headers.Remove("X-Gateway-Token");
        }
    }
}
