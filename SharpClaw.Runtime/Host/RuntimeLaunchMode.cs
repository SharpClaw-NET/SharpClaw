using Microsoft.Extensions.Configuration;

namespace SharpClaw.Runtime.Host;

public enum RuntimeLaunchMode
{
    Local,
    RemoteProxy,
    PairingClient,
}

public sealed record RuntimeLaunchPlan(
    RuntimeLaunchMode Mode,
    RemoteProxyOptions? RemoteProxyOptions)
{
    public static RuntimeLaunchPlan From(
        IReadOnlyList<string> args,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = RemoteProxyOptions.Bind(configuration);
        var pairingRequested = args.Any(static arg =>
            string.Equals(arg, "--pair", StringComparison.OrdinalIgnoreCase));

        if (options is null)
        {
            if (pairingRequested)
            {
                throw new InvalidOperationException(
                    "The --pair composition requires enabled Runtime:RemoteProxy options.");
            }

            return new RuntimeLaunchPlan(RuntimeLaunchMode.Local, null);
        }

        return new RuntimeLaunchPlan(
            pairingRequested ? RuntimeLaunchMode.PairingClient : RuntimeLaunchMode.RemoteProxy,
            options);
    }

    public RemoteProxyOptions RequireRemoteProxyOptions()
        => RemoteProxyOptions ?? throw new InvalidOperationException(
            "Remote Runtime composition requires enabled Runtime:RemoteProxy options.");
}
