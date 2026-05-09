using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Helpers;

namespace SharpClaw.Services;

/// <summary>
/// Client-side cache of typed frontend contributions declared by enabled
/// modules. Uno talks directly to the internal API for this data; gateway
/// proxying is deliberately not part of this path.
/// </summary>
internal sealed class ModuleFrontendContributionRegistry(ModuleStateCache modules)
{
    private IReadOnlyList<ModuleFrontendContribution> _items = [];

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

            _items = response.Items;
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
            || modules.IsEnabled(requiredModuleId);
    }
}
