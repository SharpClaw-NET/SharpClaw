namespace SharpClaw.Services;

/// <summary>
/// Coordinates frontend module state caches that must stay in sync after login,
/// setup, scan, enable, disable, reload, and unload operations.
/// </summary>
internal sealed class ModuleFrontendStateService(
    ModuleStateCache moduleStates,
    ModuleFrontendContributionRegistry contributions)
{
    public ModuleStateCache ModuleStates => moduleStates;

    public ModuleFrontendContributionRegistry Contributions => contributions;

    public async Task RefreshAsync(SharpClawApiClient api, CancellationToken ct = default)
    {
        await moduleStates.RefreshAsync(api, ct);
        await contributions.RefreshAsync(api, ct);
    }
}
