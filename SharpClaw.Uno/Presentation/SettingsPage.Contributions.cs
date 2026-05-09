using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class SettingsPage
{
    private readonly Dictionary<string, ModuleFrontendContribution> _moduleContributionTabs =
        new(StringComparer.Ordinal);

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
        if (!TabRequiredModules.ContainsKey(candidate)
            && !_moduleContributionTabs.ContainsKey(candidate)
            && _cachedModuleStates?.Any(m => m.DisplayName == candidate) != true)
            return candidate;

        var suffix = 2;
        while (true)
        {
            var next = $"{candidate} {suffix}";
            if (!TabRequiredModules.ContainsKey(next)
                && !_moduleContributionTabs.ContainsKey(next)
                && _cachedModuleStates?.Any(m => m.DisplayName == next) != true)
                return next;
            suffix++;
        }
    }

    private async Task LoadContributionSettingsAsync(ModuleFrontendContribution contribution)
    {
        ContentPanel.Children.Clear();
        H(contribution.Label);
        if (!string.IsNullOrWhiteSpace(contribution.Tooltip))
            Lbl(contribution.Tooltip!, 0x808080);

        if (contribution.List is not null
            && (contribution.BuilderKey.Equals("resource-list", StringComparison.OrdinalIgnoreCase)
                || contribution.BuilderKey.Equals("model-list", StringComparison.OrdinalIgnoreCase)
                || contribution.BuilderKey.Equals("generic-list", StringComparison.OrdinalIgnoreCase)))
        {
            await LoadContributionListAsync(contribution);
            return;
        }

        Lbl($"Unsupported module UI builder '{contribution.BuilderKey}'.", 0xFF8800);
    }

    private async Task LoadContributionListAsync(ModuleFrontendContribution contribution)
    {
        var list = contribution.List;
        if (list is null)
        {
            Lbl("This contribution did not declare a list endpoint.", 0xFF8800);
            return;
        }

        if (!string.IsNullOrWhiteSpace(list.SyncInternalApiPath))
        {
            var syncBtn = GreenButton("Refresh");
            syncBtn.Click += async (_, _) =>
            {
                syncBtn.IsEnabled = false;
                try
                {
                    using var syncResp = await Api.PostAsync(list.SyncInternalApiPath, null);
                    Status(syncResp.IsSuccessStatusCode ? "Refresh complete." : "Refresh failed.",
                        syncResp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
                    await LoadContributionSettingsAsync(contribution);
                }
                finally
                {
                    syncBtn.IsEnabled = true;
                }
            };
            ContentPanel.Children.Add(syncBtn);
        }

        try
        {
            using var resp = await Api.GetAsync(list.ListInternalApiPath);
            if (!resp.IsSuccessStatusCode)
            {
                Lbl($"Failed to load {contribution.Label}: {(int)resp.StatusCode}", 0xFF4444);
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var rows = ExtractContributionRows(doc.RootElement).ToList();
            if (rows.Count == 0)
            {
                Lbl(list.EmptyText ?? "(none)", 0x555555);
                return;
            }

            var panel = new StackPanel { Spacing = 2 };
            foreach (var row in rows)
            {
                var id = ReadString(row, "id", "modelId", "resourceId");
                var label = ReadString(row, "name", "displayName", "title", "modelId", "id") ?? "(unnamed)";
                var detail = BuildContributionDetail(row, list.Columns);
                var status = ReadString(row, "status", "state");

                Func<Task>? onDelete = null;
                if (!string.IsNullOrWhiteSpace(id)
                    && !string.IsNullOrWhiteSpace(list.DeleteInternalApiPathTemplate))
                {
                    var path = list.DeleteInternalApiPathTemplate.Replace("{id}", Uri.EscapeDataString(id));
                    onDelete = async () =>
                    {
                        using var delResp = await Api.DeleteAsync(path);
                        if (delResp.IsSuccessStatusCode)
                            await LoadContributionSettingsAsync(contribution);
                        else
                            Status($"Delete failed: {(int)delResp.StatusCode}", 0xFF4444);
                    };
                }

                panel.Children.Add(MakeListRow(label, detail, null, onDelete, status, 0x808080));
            }
            ContentPanel.Children.Add(panel);
        }
        catch (Exception ex)
        {
            Lbl(ex.Message, 0xFF4444);
        }
    }

    private static IEnumerable<JsonElement> ExtractContributionRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var key in new[] { "items", "models", "resources", "data" })
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in value.EnumerateArray())
                yield return item;
            yield break;
        }
    }

    private static string? BuildContributionDetail(
        JsonElement row,
        IReadOnlyList<ModuleFrontendListColumn>? columns)
    {
        if (columns is { Count: > 0 })
        {
            var parts = columns
                .Select(column => (column.Label, Value: ReadString(row, column.Key)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => $"{item.Label}: {item.Value}");
            var detail = string.Join("  ", parts);
            if (!string.IsNullOrWhiteSpace(detail)) return detail;
        }

        var fallback = new[]
        {
            ReadString(row, "providerName", "provider", "type"),
            ReadString(row, "path", "filePath", "sourceUrl"),
            ReadString(row, "description"),
        }.Where(value => !string.IsNullOrWhiteSpace(value));

        var text = string.Join("  ", fallback);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ReadString(JsonElement row, params string[] names)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                return value.ToString();
        }
        return null;
    }
}
