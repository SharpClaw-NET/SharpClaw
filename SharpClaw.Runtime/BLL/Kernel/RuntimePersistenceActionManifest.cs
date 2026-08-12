using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

public static class RuntimePersistenceActionManifest
{
    public static IReadOnlyList<SharpClawActionKey> Required { get; } =
        SharpClawActionCatalog.Kernel
            .Where(static key =>
                key.Value.StartsWith("storage.", StringComparison.Ordinal)
                && !key.Value.StartsWith(
                    "storage.transaction.",
                    StringComparison.Ordinal))
            .ToArray();

    public static bool Contains(SharpClawActionKey key) => Required.Contains(key);

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
                "The persistence action graph is incomplete. Missing actions: "
                + string.Join(", ", missing));
        }
    }
}
