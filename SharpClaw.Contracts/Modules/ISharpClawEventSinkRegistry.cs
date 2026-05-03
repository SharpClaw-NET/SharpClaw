namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Host-side registry of <see cref="ISharpClawEventSink"/> instances.
/// Modules that contribute a sink whose lifetime tracks an external state
/// (for example a trigger source that only wants events while bindings are
/// loaded) call <see cref="InvalidateCache"/> when their effective subscription
/// changes so the dispatcher rebuilds its sink list on the next dispatch.
/// </summary>
public interface ISharpClawEventSinkRegistry
{
    /// <summary>
    /// Drop any cached sink list so the next dispatch re-resolves the
    /// current set of registered sinks from DI.
    /// </summary>
    void InvalidateCache();
}
