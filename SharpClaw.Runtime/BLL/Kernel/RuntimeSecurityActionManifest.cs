using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>
/// Lists the security actions declared by the published action catalog.
/// The list is derived from the package catalog and does not define new keys.
/// </summary>
public static class RuntimeSecurityActionManifest
{
    public static IReadOnlyList<SharpClawActionKey> Required { get; } =
        SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith("security.", StringComparison.Ordinal))
            .ToArray();

    public static bool Contains(SharpClawActionKey key) =>
        Required.Contains(key);
}

/// <summary>Redacted input passed to one Runtime security action.</summary>
public sealed record RuntimeSecurityActionInvocation(
    string Operation,
    string Resource);
