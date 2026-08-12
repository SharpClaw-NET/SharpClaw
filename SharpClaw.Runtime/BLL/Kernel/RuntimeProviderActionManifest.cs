using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>
/// Lists the provider actions published by the Core action catalog.
/// The Runtime validates this boundary before it binds provider credentials.
/// </summary>
public static class RuntimeProviderActionManifest
{
    public static IReadOnlyList<SharpClawActionKey> Required { get; } =
        SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith("provider.", StringComparison.Ordinal))
            .ToArray();

    public static bool Contains(SharpClawActionKey key) =>
        Required.Contains(key);

    internal static void Validate(KernelGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var missing = Required
            .Where(key => !graph.ContainsAction(key))
            .Select(static key => key.Value)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "The provider action graph is incomplete. Missing actions: " +
                string.Join(", ", missing));
        }
    }
}
