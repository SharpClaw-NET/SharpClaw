namespace SharpClaw.Runtime.Host;

public static class RuntimeLauncher
{
    public static Task<bool> TryRunEarlyAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        _ = RuntimeLaunchPlan.From(args);
        return Task.FromResult(false);
    }
}
