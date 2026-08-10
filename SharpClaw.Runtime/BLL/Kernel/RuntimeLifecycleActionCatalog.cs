using SharpClaw.Contracts.Modules;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Identifies the Runtime-owned lifecycle actions in the published action catalog.</summary>
internal static class RuntimeLifecycleActionCatalog
{
    public static readonly SharpClawActionKey StartPrepare = Find("runtime.start.prepare");
    public static readonly SharpClawActionKey StartConfigure = Find("runtime.start.configure");
    public static readonly SharpClawActionKey StartBind = Find("runtime.start.bind");
    public static readonly SharpClawActionKey StopPrepare = Find("runtime.stop.prepare");
    public static readonly SharpClawActionKey StopComplete = Find("runtime.stop.complete");

    public static IReadOnlyList<SharpClawActionKey> All { get; } =
    [
        StartPrepare,
        StartConfigure,
        StartBind,
        StopPrepare,
        StopComplete,
    ];

    public static bool Contains(SharpClawActionKey key) =>
        All.Any(candidate => candidate.Equals(key));

    private static SharpClawActionKey Find(string value) =>
        SharpClawActionCatalog.Kernel.Single(key =>
            string.Equals(key.Value, value, StringComparison.Ordinal));
}
