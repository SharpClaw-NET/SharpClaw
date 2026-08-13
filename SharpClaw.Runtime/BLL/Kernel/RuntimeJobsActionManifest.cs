using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Lists the package-owned Jobs action catalog used by the Runtime host.</summary>
public static class RuntimeJobsActionManifest
{
    public static IReadOnlyList<SharpClawActionKey> Required =>
        SharpClawActionCatalog.Jobs;

    public static IReadOnlyList<string> Families =>
        SharpClawActionCatalog.JobsFamilies;

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
                "The Jobs action graph is incomplete. Missing actions: " +
                string.Join(", ", missing));
        }

        if (Required.Count != 138 || Families.Count != 46)
        {
            throw new InvalidOperationException(
                "The package-owned Jobs catalog does not contain its required 46 families and 138 keys.");
        }
    }
}
