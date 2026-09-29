namespace SharpClaw.Runtime.Host;

public static class RuntimeLauncher
{
    internal const string SidecarModeArgument = "--sharpclaw-sidecar-host";

    public static async Task<bool> TryRunEarlyAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 1 && string.Equals(args[0], SidecarModeArgument, StringComparison.Ordinal))
        {
            await using var sidecar = await SharpClaw.SidecarHost.OutOfProcess.OutOfProcessModuleServer
                .CreateAsync([], cancellationToken);
            await sidecar.RunAsync(cancellationToken);
            return true;
        }

        _ = RuntimeLaunchPlan.From(args);
        return false;
    }
}
