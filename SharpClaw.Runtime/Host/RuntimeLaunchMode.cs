namespace SharpClaw.Runtime.Host;

public enum RuntimeLaunchMode
{
    Local,
}

public sealed record RuntimeLaunchPlan(RuntimeLaunchMode Mode)
{
    public static RuntimeLaunchPlan From(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return new RuntimeLaunchPlan(RuntimeLaunchMode.Local);
    }
}
