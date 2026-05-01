namespace SharpClaw.Gateway.Modules.Hosting;

/// <summary>
/// Outcome of a single <see cref="GatewayModuleHostManager"/> operation.
/// Returned to the caller (initial mapping, options-monitor callback, or a
/// future <c>ModuleSync</c> poller) so each toggle can be logged with its
/// effective result.
/// </summary>
public enum ModuleHostChange
{
    /// <summary>The host's state already matched the requested state.</summary>
    NoChange,

    /// <summary>The module was loaded and its enabled groups were mapped.</summary>
    Enabled,

    /// <summary>The module was unmapped, drained, and disposed.</summary>
    Disabled,

    /// <summary>The module was disabled and re-enabled in a single operation.</summary>
    Reloaded,

    /// <summary>
    /// The operation could not complete because another module already owns
    /// the same prefix. The previous state is preserved.
    /// </summary>
    Conflict,

    /// <summary>The module's load or map step threw; the previous state is preserved.</summary>
    Failed,

    /// <summary>The module id is not known to the loader.</summary>
    NotFound,
}
