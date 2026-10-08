using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Defines the Runtime-owned effect that follows short startup preparation.</summary>
public static class RuntimeStartupActionDefinitions
{
    internal const string SourceId = "runtime-startup";

    /// <summary>
    /// Initializes the selected persistence provider once. The input identifies
    /// the preceding preparation action; it contains no configuration or secrets.
    /// Modules may inspect, wrap, observe or cancel, but not replace or repeat it.
    /// </summary>
    public static ActionDescriptor<string, bool> Initialize { get; } = new(
        new SharpClawActionKey("runtime.start.initialize"),
        1,
        "runtime startup",
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.Wrap |
        ActionInterceptionCapabilities.Observe |
        ActionInterceptionCapabilities.Cancel,
        ContainsSensitiveData: false,
        HasIrreversibleEffects: true,
        new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "invocation"),
        ContinuationPolicy: null,
        TimeSpan.FromMinutes(2))
    {
        SafePoints =
        [
            ActionSafePoint.BeforeContinuation,
            ActionSafePoint.BeforeTerminal,
            ActionSafePoint.AfterTerminal,
            ActionSafePoint.BeforeCommit,
            ActionSafePoint.AfterCommit,
        ],
    };
}
