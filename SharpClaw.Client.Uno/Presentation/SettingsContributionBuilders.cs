using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using SharpClaw.Contracts.Modules;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

internal sealed record SettingsContributionBuildContext(
    SharpClawApiClient Api,
    StackPanel Container,
    ModuleFrontendContribution Contribution,
    Action<string, int> Label,
    Action<string, int> Status,
    Func<string, Button> CreatePrimaryButton,
    Func<string, string?, Action?, Func<Task>?, string?, int, Grid> CreateListRow,
    Func<Task> RebuildAsync,
    CancellationToken CancellationToken);

internal interface ISettingsContributionBuilder
{
    string Key { get; }

    Task BuildAsync(SettingsContributionBuildContext context);
}

internal sealed class SettingsContributionBuilderRegistry
{
    private readonly Dictionary<string, ISettingsContributionBuilder> _builders;

    private SettingsContributionBuilderRegistry(IEnumerable<ISettingsContributionBuilder> builders)
    {
        _builders = builders.ToDictionary(builder => builder.Key, StringComparer.OrdinalIgnoreCase);
    }

    public static SettingsContributionBuilderRegistry CreateDefault()
    {
        var resourceList = new ResourceListSettingsContributionBuilder("resource-list");
        return new SettingsContributionBuilderRegistry(
        [
            resourceList,
            new ResourceListSettingsContributionBuilder("model-list"),
            new ResourceListSettingsContributionBuilder("generic-list"),
        ]);
    }

    public ISettingsContributionBuilder? Resolve(string builderKey)
        => _builders.GetValueOrDefault(builderKey);
}

internal sealed class ResourceListSettingsContributionBuilder(string key) : ISettingsContributionBuilder
{
    public string Key => key;

    public async Task BuildAsync(SettingsContributionBuildContext context)
    {
        var list = context.Contribution.List;
        if (list is null)
        {
            context.Label("This contribution did not declare a list endpoint.", 0xFF8800);
            return;
        }

        if (!string.IsNullOrWhiteSpace(list.SyncInternalApiPath))
            AddSyncButton(context, list);

        try
        {
            using var resp = await context.Api.GetAsync(list.ListInternalApiPath, context.CancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                context.Label($"Failed to load {context.Contribution.Label}: {(int)resp.StatusCode}", 0xFF4444);
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(context.CancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: context.CancellationToken);
            var rows = ExtractRows(doc.RootElement).ToList();
            if (rows.Count == 0)
            {
                context.Label(list.EmptyText ?? "(none)", 0x555555);
                return;
            }

            AddLocalSelectionIfDeclared(context, rows);

            var panel = new StackPanel { Spacing = 2 };
            foreach (var row in rows)
            {
                var id = ReadString(row, "id", "modelId", "resourceId");
                var label = ReadString(row, "name", "displayName", "title", "modelId", "id") ?? "(unnamed)";
                var detail = BuildDetail(row, list.Columns);
                var status = ReadString(row, "status", "state");
                var delete = BuildDeleteAction(context, list, id);

                panel.Children.Add(context.CreateListRow(label, detail, null, delete, status, 0x808080));
            }

            context.Container.Children.Add(panel);
        }
        catch (Exception ex)
        {
            context.Label(ex.Message, 0xFF4444);
        }
    }

    private static void AddLocalSelectionIfDeclared(
        SettingsContributionBuildContext context,
        IReadOnlyList<JsonElement> rows)
    {
        var settingKey = ReadMetadataString(context.Contribution, "localSettingKey");
        if (string.IsNullOrWhiteSpace(settingKey)) return;

        context.Label(ReadMetadataString(context.Contribution, "selectionLabel") ?? "Current Selection", 0x00CCFF);
        var settings = App.Services?.GetService<ClientSettings>();
        var savedId = settings?.Get(settingKey);
        var selector = new ComboBox
        {
            FontFamily = TerminalUI.Mono,
            FontSize = 11,
            Background = TerminalUI.Brush(0x1A1A1A),
            Foreground = TerminalUI.Brush(0xCCCCCC),
            BorderBrush = TerminalUI.Brush(0x333333),
            BorderThickness = new Thickness(1),
            MinWidth = 300,
        };

        selector.Items.Add(new ComboBoxItem { Content = "(none)", Tag = string.Empty });
        var selectedIndex = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var id = ReadString(rows[i], "id", "resourceId", "modelId");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var label = ReadString(rows[i], "name", "displayName", "title", "id") ?? id;
            selector.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            if (string.Equals(savedId, id, StringComparison.OrdinalIgnoreCase))
                selectedIndex = selector.Items.Count - 1;
        }

        selector.SelectedIndex = selectedIndex;
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            settings?.Set(settingKey, string.IsNullOrWhiteSpace(value) ? null : value);
            context.Status(string.IsNullOrWhiteSpace(value) ? "Selection cleared." : "Selection saved.",
                string.IsNullOrWhiteSpace(value) ? 0x808080 : 0x00FF00);
        };
        context.Container.Children.Add(selector);
    }

    private static void AddSyncButton(SettingsContributionBuildContext context, ModuleFrontendList list)
    {
        var syncBtn = context.CreatePrimaryButton("Refresh");
        syncBtn.Click += async (_, _) =>
        {
            syncBtn.IsEnabled = false;
            try
            {
                using var syncResp = await context.Api.PostAsync(
                    list.SyncInternalApiPath!,
                    null,
                    context.CancellationToken);
                context.Status(syncResp.IsSuccessStatusCode ? "Refresh complete." : "Refresh failed.",
                    syncResp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
                await context.RebuildAsync();
            }
            finally
            {
                syncBtn.IsEnabled = true;
            }
        };
        context.Container.Children.Add(syncBtn);
    }

    private static Func<Task>? BuildDeleteAction(
        SettingsContributionBuildContext context,
        ModuleFrontendList list,
        string? id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(list.DeleteInternalApiPathTemplate))
            return null;

        var path = list.DeleteInternalApiPathTemplate.Replace("{id}", Uri.EscapeDataString(id));
        return async () =>
        {
            using var delResp = await context.Api.DeleteAsync(path, context.CancellationToken);
            if (delResp.IsSuccessStatusCode)
                await context.RebuildAsync();
            else
                context.Status($"Delete failed: {(int)delResp.StatusCode}", 0xFF4444);
        };
    }

    private static IEnumerable<JsonElement> ExtractRows(JsonElement root)
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

    private static string? BuildDetail(JsonElement row, IReadOnlyList<ModuleFrontendListColumn>? columns)
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
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }

        return null;
    }

    private static string? ReadMetadataString(ModuleFrontendContribution contribution, string key)
    {
        if (contribution.Metadata is null
            || !contribution.Metadata.TryGetValue(key, out var value)
            || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }
}
