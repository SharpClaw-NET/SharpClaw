using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Helpers;

namespace SharpClaw.Services;

/// <summary>
/// Client-side cache of typed frontend contributions declared by enabled
/// modules. Uno talks directly to the internal API for this data; gateway
/// proxying is deliberately not part of this path.
/// </summary>
internal sealed class ModuleFrontendContributionRegistry
{
    private const string StateKey = "client.frontend.contributions";
    private readonly ModuleStateCache _modules;
    private readonly ClientActionDispatcher _actions;
    private IReadOnlyList<ModuleFrontendContribution> _items = [];

    public ModuleFrontendContributionRegistry(
        ModuleStateCache modules,
        ClientActionDispatcher actions)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public IReadOnlyList<ModuleFrontendContribution> GetAll()
        => _items;

    public IReadOnlyList<ModuleFrontendContribution> GetActive(FrontendContributionPoint point)
        => [.. _items
            .Where(item => item.Point == point)
            .Where(IsRequiredModuleEnabled)
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)];

    public async Task RefreshAsync(SharpClawApiClient api, CancellationToken ct = default)
    {
        try
        {
            using var resp = await api.GetAsync("/modules/frontend-contributions", ct);
            if (!resp.IsSuccessStatusCode) return;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var response = await JsonSerializer.DeserializeAsync<ModuleFrontendContributionResponse>(stream, TerminalUI.Json, ct);
            if (response is null) return;

            var expectedVersion = _actions.GetStateVersion(StateKey);
            await _actions.CommitStateAsync(
                StateKey,
                expectedVersion,
                _ =>
                {
                    _items = response.Items;
                    return ValueTask.CompletedTask;
                },
                ct);
        }
        catch
        {
            // API unreachable: keep the last successful contribution snapshot.
        }
    }

    private bool IsRequiredModuleEnabled(ModuleFrontendContribution item)
    {
        var requiredModuleId = string.IsNullOrWhiteSpace(item.RequiredModuleId)
            ? item.ModuleId
            : item.RequiredModuleId;

        return string.IsNullOrWhiteSpace(requiredModuleId)
            || _modules.IsEnabled(requiredModuleId);
    }
}
