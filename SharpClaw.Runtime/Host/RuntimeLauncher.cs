using Microsoft.Extensions.Configuration;
using SharpClaw.Runtime.INF.Configuration;

namespace SharpClaw.Runtime.Host;

public static class RuntimeLauncher
{
    public static async Task<bool> TryRunEarlyAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var instancePaths = RuntimeInstancePathResolver.CreateBackend();
        var isDevelopment = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Development",
                StringComparison.OrdinalIgnoreCase);
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddLocalEnvironment(isDevelopment, instancePaths)
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
