namespace SharpClaw.Runtime.Host;

public static class RuntimePairingClient
{
    public static Task RunAsync(
        RuntimeLaunchPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Mode != RuntimeLaunchMode.PairingClient)
            throw new ArgumentException("The launch plan is not PairingClient mode.", nameof(plan));

        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "The Runtime pairing client is not configured in this composition boundary.");
    }
}
