namespace SharpClaw.Runtime.Host;

internal enum RuntimeLaunchMode
{
    Local,
}

internal sealed record RuntimeLaunchPlan(RuntimeLaunchMode Mode)
{
    public static RuntimeLaunchPlan From(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return new RuntimeLaunchPlan(RuntimeLaunchMode.Local);
    }
}
