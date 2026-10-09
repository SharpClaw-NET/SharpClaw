using System.Runtime.ExceptionServices;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Gateway.Infrastructure;


internal static class GatewayActionManifest
{
    public static IReadOnlyList<SharpClawActionKey> Required { get; } =
        SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith(
                "gateway.",
                StringComparison.Ordinal))
            .ToArray();

    public static IReadOnlyList<SharpClawActionKey> BackgroundRequired { get; } =
        SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith(
                "background.",
                StringComparison.Ordinal))
            .ToArray();

    public static IReadOnlyList<SharpClawActionKey> Published { get; } =
        Required.Concat(BackgroundRequired).ToArray();

    public static void Validate(KernelGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var missing = Published
            .Where(key => !graph.ContainsAction(key))
            .Select(static key => key.Value)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "The Gateway action graph is incomplete. Missing actions: " +
                string.Join(", ", missing));
        }
    }
}
