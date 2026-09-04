using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

internal static class RuntimeChatScenarioManifest
{
    internal static IReadOnlyList<SharpClawActionKey> RequiredActions { get; } =
        SharpClawActionCatalog.Kernel
            .Where(static key =>
                key.Value.StartsWith("chat.", StringComparison.Ordinal)
                || key.Value.StartsWith("conversation.", StringComparison.Ordinal))
            .ToArray();

    internal static IReadOnlyList<string> RequiredScenarios { get; } =
    [
        "buffered-turn",
        "streaming-turn",
        "conversation-history",
        "stream-cancellation",
        "stream-failure",
    ];

    internal static bool Contains(SharpClawActionKey key) =>
        RequiredActions.Contains(key);
}
