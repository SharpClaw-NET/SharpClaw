using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Gateway.Configuration;

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
        bridgeApp.UseHttpsRedirection();
        return bridgeApp;
    }

    private static void RequireApprovedPair(RemoteRuntimeBridgeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PairingFile))
        {
            throw new InvalidOperationException(
                "The Runtime bridge requires an approved pairing before binding.");
        }
    }
}
