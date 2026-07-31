using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Gateway.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace SharpClaw.Gateway.RemoteRuntimeBridge;

internal interface IRemoteRuntimeBridgeListener;

internal sealed class RemoteRuntimeBridgeListener : IRemoteRuntimeBridgeListener;

internal static class RemoteRuntimeBridgeHost
{
    public static void RegisterServices(
        IServiceCollection services,
        RemoteRuntimeBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            return;

        services.AddSingleton<IRemoteRuntimeBridgeListener, RemoteRuntimeBridgeListener>();
    }

    public static WebApplication MapRemoteRuntimeBridge(
        WebApplication bridgeApp,
        RemoteRuntimeBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(bridgeApp);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            return bridgeApp;

        RequireApprovedPair(options);
        bridgeApp.MapForwarder(
            "/{**catch-all}",
            options.TargetBaseUrl!,
            ForwarderRequestConfig.Empty,
            new RemoteRuntimeBridgeTransformer(options.AuthoritativeApiKey!));
        return bridgeApp;
    }

    public static WebApplication Build(
        string[] args,
        RemoteRuntimeBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireApprovedPair(options);

        var listenUri = new Uri(options.ListenUrl, UriKind.Absolute);
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ListenAnyIP(
                listenUri.Port,
                listenOptions =>
                {
                    if (!string.Equals(listenUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The Runtime bridge listener must use HTTPS.");
                    }

                    listenOptions.UseHttps(options.ServerCertificatePath!);
                });
        });
        builder.Services.AddReverseProxy();

        var bridgeApp = builder.Build();
        return MapRemoteRuntimeBridge(bridgeApp, options);
    }

    private static void RequireApprovedPair(RemoteRuntimeBridgeOptions options)
    {
        _ = options;
        throw new InvalidOperationException(
            "The Runtime bridge requires a verified administrator-approved pairing before binding.");
    }

    private sealed class RemoteRuntimeBridgeTransformer(string authoritativeApiKey) : HttpTransformer
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
            proxyRequest.Headers.TryAddWithoutValidation("X-Api-Key", authoritativeApiKey);
        }
    }
}
