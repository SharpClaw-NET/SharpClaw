namespace SharpClaw.Application.Core.DefaultResources;

internal static class CoreDefaultResourceKeys
{
    public const string Agent = "agent";

    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        Agent
    };

    public static IReadOnlyCollection<string> All => Keys;

    public static bool Contains(string key) => Keys.Contains(key);
}
