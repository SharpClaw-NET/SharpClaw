namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Flags enum of host events that modules can observe via <see cref="ISharpClawEventSink"/>.
/// A module's <see cref="ISharpClawEventSink.SubscribedEvents"/> declares
/// which events it wants to receive.
/// </summary>
[Flags]
public enum SharpClawEventType : long
{
    None = 0,

    // Module lifecycle — the only events the host actually raises today.
    ModuleEnabled       = 1L << 14,
    ModuleDisabled      = 1L << 15,
    ModuleHealthFailed  = 1L << 16,

    // Convenience groups
    AllModuleEvents     = ModuleEnabled | ModuleDisabled | ModuleHealthFailed,
    All                 = ~None,
}

/// <summary>
/// Immutable event payload dispatched to <see cref="ISharpClawEventSink"/> observers.
/// </summary>
public sealed record SharpClawEvent(
    /// <summary>Event type identifier.</summary>
    SharpClawEventType Type,

    /// <summary>UTC timestamp of the event.</summary>
    DateTimeOffset Timestamp,

    /// <summary>
    /// Primary entity ID (agent ID, job ID, channel ID, etc.).
    /// Null for events without a specific entity.
    /// </summary>
    Guid? EntityId = null,

    /// <summary>
    /// Optional secondary entity ID (e.g. channel ID when the primary is agent ID).
    /// </summary>
    Guid? SecondaryEntityId = null,

    /// <summary>
    /// Module ID for module lifecycle events; agent action key for job events.
    /// Null when not applicable.
    /// </summary>
    string? SourceId = null,

    /// <summary>
    /// Optional human-readable summary (e.g. "Agent 'Helper' created",
    /// "Job cu_enumerate_windows completed in 1.2s"). For logging and
    /// display — not for parsing.
    /// </summary>
    string? Summary = null,

    /// <summary>
    /// Optional structured data. Contents vary by event type.
    /// Sinks should handle missing keys gracefully.
    /// </summary>
    IReadOnlyDictionary<string, object>? Data = null
);
