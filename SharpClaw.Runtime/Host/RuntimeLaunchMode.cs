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
    string? PairingFile,
    string? LocalUrl)
{
    public static RuntimeLaunchPlan From(
        IReadOnlyList<string> args,
        IConfiguration configuration)
    {
        if (args.Any(static arg => string.Equals(arg, "--pair", StringComparison.OrdinalIgnoreCase)))
        {
            return new RuntimeLaunchPlan(
                RuntimeLaunchMode.PairingClient,
                configuration["Runtime:RemoteProxy:PairingFile"],
                configuration["Runtime:RemoteProxy:LocalUrl"]);
        }

        var configuredMode = configuration["Runtime:Mode"]
            ?? Environment.GetEnvironmentVariable("SHARPCLAW_RUNTIME_MODE");

        var mode = configuredMode switch
        {
            null or "" or "Local" => RuntimeLaunchMode.Local,
            "RemoteProxy" => RuntimeLaunchMode.RemoteProxy,
            _ => throw new InvalidOperationException(
                $"Unsupported Runtime:Mode '{configuredMode}'. Supported values are Local and RemoteProxy."),
        };

        return new RuntimeLaunchPlan(
            mode,
            configuration["Runtime:RemoteProxy:PairingFile"],
            configuration["Runtime:RemoteProxy:LocalUrl"]);
    }
}
