using Microsoft.Extensions.Configuration;

namespace SharpClaw.Runtime.Host;

public static class RuntimeLauncher
{
    public static async Task<bool> TryRunEarlyAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        var plan = RuntimeLaunchPlan.From(args, configuration);

        switch (plan.Mode)
        {
            case RuntimeLaunchMode.Local:
                return false;
            case RuntimeLaunchMode.RemoteProxy:
                await RemoteProxyHost.RunAsync(plan, cancellationToken);
                return true;
            case RuntimeLaunchMode.PairingClient:
                await RuntimePairingClient.RunAsync(plan, cancellationToken);
                return true;
            default:
                throw new InvalidOperationException($"Unsupported runtime launch mode: {plan.Mode}.");
        }
    }
}
