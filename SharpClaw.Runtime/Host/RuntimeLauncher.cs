namespace SharpClaw.Runtime.Host;

internal static class RuntimeLauncher
{
    internal const string SidecarModeArgument = "--sharpclaw-sidecar-host";

    public static async Task<bool> TryRunEarlyAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 1 && string.Equals(args[0], SidecarModeArgument, StringComparison.Ordinal))
        {
            var sidecar = await SharpClaw.SidecarHost.OutOfProcess.OutOfProcessModuleServer
                .CreateAsync([], cancellationToken).ConfigureAwait(false);
            await using var sidecarAsyncDisposal = sidecar.ConfigureAwait(false);
            await sidecar.RunAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        _ = RuntimeLaunchPlan.From(args);
        return false;
    }
}
