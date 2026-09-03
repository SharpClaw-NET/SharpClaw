namespace SharpClaw.Services;

/// <summary>
/// Coordinates frontend module state caches that must stay in sync after login,
/// setup, scan, enable, disable, reload, and unload operations.
/// </summary>
internal sealed class ModuleFrontendStateService(
    ModuleStateCache moduleStates)
{
    public ModuleStateCache ModuleStates => moduleStates;

    public Task RefreshAsync(SharpClawApiClient api, CancellationToken ct = default)
        => moduleStates.RefreshAsync(api, ct);
}
