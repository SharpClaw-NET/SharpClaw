using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;

namespace SharpClaw.Helpers;

/// <summary>
/// Shared terminal-style UI constants, cached brushes, and permission metadata
/// used across MainPage, SettingsPage, and FirstSetupPage.
/// </summary>
internal static class TerminalUI
{
    // ── JSON ─────────────────────────────────────────────────────
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
    // ── Fonts ────────────────────────────────────────────────────
    public static readonly FontFamily Mono = new("Consolas, Courier New, monospace");

    // ── Brush cache ─────────────────────────────────────────────
    public static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);
    private static readonly Dictionary<int, SolidColorBrush> _brushCache = [];

    public static SolidColorBrush Brush(int rgb)
    {
        if (!_brushCache.TryGetValue(rgb, out var brush))
        {
            brush = new SolidColorBrush(ColorFrom(rgb));
            _brushCache[rgb] = brush;
        }
        return brush;
    }

    public static Windows.UI.Color ColorFrom(int rgb)
        => Windows.UI.Color.FromArgb(255,
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));

    // ── Wildcard resource ID ────────────────────────────────────
    public static readonly Guid AllResourcesId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    // ── Permission metadata ─────────────────────────────────────

    public static readonly (string Tag, string Label)[] ClearanceOptions =
    [
        ("Independent",                "Can act without approval"),
        ("ApprovedByWhitelistedAgent", "Only with approval from a managing agent"),
        ("ApprovedByPermittedAgent",   "Only with approval from an agent that has clearance to act"),
        ("ApprovedByWhitelistedUser",  "Only with approval from a user"),
        ("ApprovedBySameLevelUser",    "Only with approval from a user that can grant the permission"),
        ("Restricted",                 "Hard denied - action is blocked regardless of other layers"),
    ];

    // ── Helpers ──────────────────────────────────────────────────

    public static string FormatFlagName(string flagKey)
    {
        var s = flagKey.AsSpan();
        if (s.StartsWith("Can")) s = s[3..];
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]))
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpper(s[i]) : s[i]);
        }
        return sb.ToString();
    }

    public static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\u2026";

    public static void CopyToClipboard(string text)
    {
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    // ── Reusable control factories ──────────────────────────────

    /// <summary>Creates a clearance <see cref="ComboBox"/> pre-populated with the standard options.</summary>
    public static ComboBox MakeClearanceCombo(string selected, bool includeUnset = true)
    {
        var box = new ComboBox
        {
            FontFamily = Mono, FontSize = 10,
            Background = Brush(0x1A1A1A), Foreground = Brush(0xCCCCCC),
            BorderBrush = Brush(0x333333), BorderThickness = new Thickness(1),
            MinWidth = 280, Padding = new Thickness(4, 2),
        };
        var selIdx = 0;
        var idx = 0;
        if (includeUnset)
        {
            box.Items.Add(new ComboBoxItem { Content = "Unset", Tag = "Unset" });
            if (string.Equals("Unset", selected, StringComparison.OrdinalIgnoreCase)) selIdx = 0;
            idx = 1;
        }
        for (var i = 0; i < ClearanceOptions.Length; i++, idx++)
        {
            box.Items.Add(new ComboBoxItem { Content = ClearanceOptions[i].Label, Tag = ClearanceOptions[i].Tag });
            if (string.Equals(ClearanceOptions[i].Tag, selected, StringComparison.OrdinalIgnoreCase)) selIdx = idx;
        }
        box.SelectedIndex = selIdx;
        return box;
    }

    /// <summary>Creates a small "✕" remove button in terminal style.</summary>
    public static Button RemoveButton(Action onClick)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = "\u2715", FontFamily = Mono, FontSize = 10, Foreground = Brush(0xFF4444) },
            Background = Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4), MinWidth = 0, MinHeight = 0,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // ── Dynamic module permission metadata ──────────────────────

    /// <summary>Cached permission metadata grouped by module. <c>null</c> until first fetch.</summary>
    private static List<ModulePermissionMetadata>? _cachedPermMetadata;

    /// <summary>
    /// Fetch permission metadata from the API, grouping global flags and resource
    /// types by owning module. Results are cached; call with <paramref name="forceRefresh"/>
    /// to re-fetch.
    /// </summary>
    public static async Task<List<ModulePermissionMetadata>> LoadPermissionMetadataAsync(
        SharpClaw.Services.SharpClawApiClient api, bool forceRefresh = false)
    {
        if (_cachedPermMetadata is not null && !forceRefresh)
            return _cachedPermMetadata;

        try
        {
            using var resp = await api.GetAsync("/modules/permissions-metadata");
            if (!resp.IsSuccessStatusCode)
                return _cachedPermMetadata ?? [];

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("modules", out var modulesArr))
                return _cachedPermMetadata ?? [];

            var result = new List<ModulePermissionMetadata>();
            foreach (var m in modulesArr.EnumerateArray())
            {
                var entry = new ModulePermissionMetadata
                {
                    ModuleId = m.GetProperty("moduleId").GetString() ?? "",
                    DisplayName = m.GetProperty("displayName").GetString() ?? "",
                    Enabled = m.TryGetProperty("enabled", out var en) && en.GetBoolean(),
                    GlobalFlags = [],
                    ResourceTypes = [],
                    DependsOn = [],
                    Description = m.TryGetProperty("description", out var descProp) && descProp.ValueKind == System.Text.Json.JsonValueKind.String ? descProp.GetString() : null,
                    Author = m.TryGetProperty("author", out var authProp) && authProp.ValueKind == System.Text.Json.JsonValueKind.String ? authProp.GetString() : null,
                    License = m.TryGetProperty("license", out var licProp) && licProp.ValueKind == System.Text.Json.JsonValueKind.String ? licProp.GetString() : null,
                    Version = m.TryGetProperty("version", out var verProp) && verProp.ValueKind == System.Text.Json.JsonValueKind.String ? verProp.GetString() : null,
                    Platforms = m.TryGetProperty("platforms", out var platProp) && platProp.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? platProp.EnumerateArray().Select(p => p.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                        : null,
                };

                if (m.TryGetProperty("globalFlags", out var flags) && flags.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var f in flags.EnumerateArray())
                        entry.GlobalFlags.Add(new ModulePermissionMetadata.FlagEntry(
                            f.GetProperty("flagKey").GetString() ?? "",
                            f.GetProperty("displayName").GetString() ?? "",
                            f.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""));

                if (m.TryGetProperty("resourceTypes", out var res) && res.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var r in res.EnumerateArray())
                        entry.ResourceTypes.Add(new ModulePermissionMetadata.ResourceTypeEntry(
                            r.GetProperty("resourceType").GetString() ?? "",
                            r.GetProperty("grantLabel").GetString() ?? "",
                            r.TryGetProperty("defaultResourceKey", out var keyProp)
                                && keyProp.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? keyProp.GetString()
                                    : null));

                if (m.TryGetProperty("dependsOn", out var deps) && deps.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var d in deps.EnumerateArray())
                        if (d.GetString() is { } depId)
                            entry.DependsOn.Add(depId);

                result.Add(entry);
            }

            _cachedPermMetadata = result;
            return result;
        }
        catch
        {
            return _cachedPermMetadata ?? [];
        }
    }

    /// <summary>
    /// Topologically sort modules so dependencies come before dependents.
    /// Modules with no dependencies come first.
    /// </summary>
    public static List<ModulePermissionMetadata> TopologicalSort(List<ModulePermissionMetadata> modules)
    {
        var byId = modules.ToDictionary(m => m.ModuleId, StringComparer.Ordinal);
        var inDegree = modules.ToDictionary(m => m.ModuleId, _ => 0, StringComparer.Ordinal);

        foreach (var m in modules)
            foreach (var dep in m.DependsOn)
                if (inDegree.ContainsKey(dep))
                    inDegree[m.ModuleId]++;

        var queue = new Queue<string>(modules.Where(m => inDegree[m.ModuleId] == 0).Select(m => m.ModuleId));
        var sorted = new List<ModulePermissionMetadata>(modules.Count);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            sorted.Add(byId[id]);

            foreach (var m in modules)
            {
                if (!m.DependsOn.Contains(id)) continue;
                inDegree[m.ModuleId]--;
                if (inDegree[m.ModuleId] == 0)
                    queue.Enqueue(m.ModuleId);
            }
        }

        // Append any remaining (circular deps — shouldn't happen)
        foreach (var m in modules)
            if (!sorted.Contains(m))
                sorted.Add(m);

        return sorted;
    }

    /// <summary>Invalidate the cached permission metadata so the next call re-fetches.</summary>
    public static void InvalidatePermissionMetadataCache() => _cachedPermMetadata = null;
}

/// <summary>
/// Client-side DTO for module permission metadata returned by
/// <c>GET /modules/permissions-metadata</c>.
/// </summary>
internal sealed class ModulePermissionMetadata
{
    public required string ModuleId { get; init; }
    public required string DisplayName { get; init; }
    public required bool Enabled { get; init; }
    public required List<FlagEntry> GlobalFlags { get; init; }
    public required List<ResourceTypeEntry> ResourceTypes { get; init; }
    public required List<string> DependsOn { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? License { get; init; }
    public string? Version { get; init; }
    public string[]? Platforms { get; init; }

    public sealed record FlagEntry(string FlagKey, string DisplayName, string Description);
    public sealed record ResourceTypeEntry(string ResourceType, string DisplayName, string? DefaultResourceKey = null);
}
