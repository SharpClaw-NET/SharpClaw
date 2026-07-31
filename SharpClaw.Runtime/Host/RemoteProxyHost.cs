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
}
