using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>
/// Selects the Runtime CLI actions published by the shared action catalog.
/// This type does not define action keys.
/// </summary>
internal static class RuntimeCliActionCatalog
{
    public static readonly SharpClawActionKey Parse = Find("runtime.cli.parse");
    public static readonly SharpClawActionKey CommandSelect = Find("runtime.cli.command.select");
    public static readonly SharpClawActionKey Execute = Find("runtime.cli.execute");
    public static readonly SharpClawActionKey OutputWrite = Find("runtime.cli.output.write");
    public static readonly SharpClawActionKey Complete = Find("runtime.cli.complete");
    public static readonly SharpClawActionKey Fail = Find("runtime.cli.fail");
    public static readonly SharpClawActionKey Cancel = Find("runtime.cli.cancel");

    public static IReadOnlyList<SharpClawActionKey> All { get; } =
    [
        Parse,
        CommandSelect,
        Execute,
        OutputWrite,
        Complete,
        Fail,
        Cancel,
    ];

    public static bool Contains(SharpClawActionKey key) =>
        All.Any(candidate => candidate.Equals(key));

    private static SharpClawActionKey Find(string value) =>
        SharpClawActionCatalog.Kernel.Single(key =>
            string.Equals(key.Value, value, StringComparison.Ordinal));
}

/// <summary>Non-secret metadata carried by one Runtime CLI action.</summary>
internal sealed record RuntimeCliActionInvocation(
    string Stage,
    string? Command,
    int ArgumentCount,
    string? FailureType = null);
