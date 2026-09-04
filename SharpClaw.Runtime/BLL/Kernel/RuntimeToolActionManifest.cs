using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Lists the published tool actions required by the Runtime graph.</summary>
public static class RuntimeToolActionManifest
{
    public static IReadOnlyList<SharpClawActionKey> Required { get; } =
        SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith("tool.", StringComparison.Ordinal))
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
                "The tool action graph is incomplete. Missing actions: " +
                string.Join(", ", missing));
        }
    }
}
