using SharpClaw.Contracts.Modules;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class SettingsPage
{
    private readonly Dictionary<string, ModuleFrontendContribution> _moduleContributionTabs =
        new(StringComparer.Ordinal);
    private readonly SettingsContributionBuilderRegistry _settingsContributionBuilders =
        SettingsContributionBuilderRegistry.CreateDefault();

    private async Task RefreshModuleFrontendStateAsync()
    {
        var moduleFrontendState = App.Services?.GetService<ModuleFrontendStateService>();
        if (moduleFrontendState is not null)
            await moduleFrontendState.RefreshAsync(Api);

        _cachedModuleStates = await FetchListAsync<ModuleStateEntry>("/modules");
    }

    private void AddContributionTabs()
    {
        var registry = App.Services?.GetService<ModuleFrontendContributionRegistry>();
        if (registry is null) return;

        var contributions = registry.GetActive(FrontendContributionPoint.SettingsPage)
            .Concat(registry.GetActive(FrontendContributionPoint.ResourcePanel))
            .ToList();
        if (contributions.Count == 0) return;

        AddTabSection("Module Features");
        foreach (var contribution in contributions)
        {
            var label = UniqueContributionLabel(contribution.Label);
            _moduleContributionTabs[label] = contribution;
            AddTabButton(label, $"sharpclaw module ui {contribution.ModuleId} {contribution.Id}");
        }
    }

    private string UniqueContributionLabel(string label)
    {
        var candidate = string.IsNullOrWhiteSpace(label) ? "Module Feature" : label.Trim();
        if (!IsReservedTabLabel(candidate)
            && !_moduleContributionTabs.ContainsKey(candidate)
            && _cachedModuleStates?.Any(m => m.DisplayName == candidate) != true)
            return candidate;

        var suffix = 2;
        while (true)
        {
            var next = $"{candidate} {suffix}";
            if (!IsReservedTabLabel(next)
                && !_moduleContributionTabs.ContainsKey(next)
                && _cachedModuleStates?.Any(m => m.DisplayName == next) != true)
                return next;
            suffix++;
        }
    }

    private static bool IsReservedTabLabel(string label)
        => label is "Providers" or "Models" or "Agents" or "Roles" or "Gateway"
            or "Manage Modules" or "Users" or "Danger Zone";

    private async Task LoadContributionSettingsAsync(ModuleFrontendContribution contribution)
    {
        ContentPanel.Children.Clear();
        H(contribution.Label);
        if (!string.IsNullOrWhiteSpace(contribution.Tooltip))
            Lbl(contribution.Tooltip!, 0x808080);

        var builder = _settingsContributionBuilders.Resolve(contribution.BuilderKey);
        if (builder is null)
        {
            Lbl($"Unsupported module UI builder '{contribution.BuilderKey}'.", 0xFF8800);
            return;
        }

        await builder.BuildAsync(new SettingsContributionBuildContext(
            Api,
            ContentPanel,
            contribution,
            Lbl,
            Status,
            GreenButton,
            MakeListRow,
            () => LoadContributionSettingsAsync(contribution),
            CancellationToken.None));
    }
}
