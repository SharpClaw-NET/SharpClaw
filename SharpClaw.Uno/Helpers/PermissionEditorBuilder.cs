using System.Text.Json;
using SharpClaw.Services;

namespace SharpClaw.Helpers;

/// <summary>
/// Shared builder that constructs a permission editor UI (global flags + per-resource grants)
/// into any <see cref="StackPanel"/>.  Used by SettingsPage (role editor), FirstSetupPage
/// (initial role grants), and MainPage channel-settings (pre-authorization overrides).
///
/// Configuration via fluent API:
/// <list type="bullet">
///   <item><see cref="WithCallerFilter"/> – restrict visible flags/resources to what the caller holds</item>
///   <item><see cref="WithExisting"/>    – pre-populate from an existing permissions JSON payload</item>
///   <item><see cref="WithFlagClearance"/>  – show per-flag clearance combo (default: true)</item>
///   <item><see cref="WithGrantClearance"/> – show per-grant clearance combo (default: true)</item>
///   <item><see cref="WithAutoSuggestBox"/> – use AutoSuggestBox instead of ComboBox for resource selectors</item>
/// </list>
/// </summary>
internal sealed class PermissionEditorBuilder
{
    private readonly SharpClawApiClient _api;

    // ── Configuration ────────────────────────────────────────────
    private JsonElement? _callerFilter;
    private JsonElement? _existing;
    private bool _flagClearance = true;
    private bool _grantClearance = true;
    private bool _useAutoSuggest;

    // ── State (populated during Build*, read during Collect*) ────
    private readonly Dictionary<string, (CheckBox Check, ComboBox? Clearance)> _flagEditors = new(7);
    private readonly Dictionary<string, StackPanel> _grantPanels = new(13);
    private readonly Dictionary<Guid, string> _nameCache = [];
    private readonly Dictionary<string, List<(Guid Id, string Name)>> _resourcesByType = [];

    public PermissionEditorBuilder(SharpClawApiClient api) => _api = api;

    // ── Fluent config ────────────────────────────────────────────

    /// <summary>Restrict visible flags and resource types to those the caller holds.</summary>
    public PermissionEditorBuilder WithCallerFilter(JsonElement? callerPermissions)
    { _callerFilter = callerPermissions; return this; }

    /// <summary>Pre-populate checkboxes and grant rows from an existing permissions payload.</summary>
    public PermissionEditorBuilder WithExisting(JsonElement? existing)
    { _existing = existing; return this; }

    /// <summary>Show a clearance combo under each global flag (default: true).</summary>
    public PermissionEditorBuilder WithFlagClearance(bool enabled)
    { _flagClearance = enabled; return this; }

    /// <summary>Show a clearance combo on each resource grant row (default: true).</summary>
    public PermissionEditorBuilder WithGrantClearance(bool enabled)
    { _grantClearance = enabled; return this; }

    /// <summary>Use <see cref="AutoSuggestBox"/> instead of <see cref="ComboBox"/> for adding resources.</summary>
    public PermissionEditorBuilder WithAutoSuggestBox(bool enabled)
    { _useAutoSuggest = enabled; return this; }

    // ── Build methods ────────────────────────────────────────────

    /// <summary>
    /// Builds the global flags section into <paramref name="container"/>.
    /// </summary>
    /// <returns><c>true</c> if at least one flag was added.</returns>
    public bool BuildGlobalFlags(StackPanel container)
    {
        _flagEditors.Clear();
        var panel = new StackPanel { Spacing = _flagClearance ? 10 : 4 };

        for (var i = 0; i < TerminalUI.GlobalFlagNames.Length; i++)
        {
            var flag = TerminalUI.GlobalFlagNames[i];

            // Caller filtering — skip flags the caller doesn't hold
            if (_callerFilter is { } cf)
            {
                var callerHas = cf.TryGetProperty(flag, out var cfp) && cfp.GetBoolean();
                if (!callerHas) continue;
            }

            var on = _existing is { } ex && ex.TryGetProperty(flag, out var fp) && fp.GetBoolean();
            var cb = new CheckBox
            {
                IsChecked = on, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(4, 0, 0, 0),
                Content = new TextBlock
                {
                    Text = TerminalUI.FormatFlagName(flag),
                    FontFamily = TerminalUI.Mono, FontSize = 11,
                    Foreground = TerminalUI.Brush(0xE0E0E0),
                },
            };
            if (TerminalUI.GlobalFlagTooltips.TryGetValue(flag, out var tip))
                ToolTipService.SetToolTip(cb, tip);

            ComboBox? clrBox = null;
            if (_flagClearance)
            {
                var clrN = TerminalUI.GlobalFlagClearanceNames[i];
                var cl = _existing is { } e2 && e2.TryGetProperty(clrN, out var cpp)
                    ? cpp.GetString() ?? "Unset" : "Unset";

                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(cb);
                row.Children.Add(new TextBlock
                {
                    Text = "Clearance:", FontFamily = TerminalUI.Mono, FontSize = 9,
                    Foreground = TerminalUI.Brush(0x808080), Margin = new Thickness(24, 2, 0, 0),
                });
                clrBox = TerminalUI.MakeClearanceCombo(cl, includeUnset: true);
                clrBox.Margin = new Thickness(24, 0, 0, 0);
                row.Children.Add(clrBox);
                panel.Children.Add(row);
            }
            else
            {
                panel.Children.Add(cb);
            }

            _flagEditors[flag] = (cb, clrBox);
        }

        container.Children.Add(panel);
        return _flagEditors.Count > 0;
    }

    /// <summary>
    /// Builds the resource grants section into <paramref name="container"/>.
    /// Performs a parallel fetch of all resource lookups.
    /// </summary>
    public async Task BuildResourceGrantsAsync(StackPanel container)
    {
        _grantPanels.Clear();
        _nameCache.Clear();
        _resourcesByType.Clear();
        await PreloadResourceNamesAsync();

        var resCont = new StackPanel { Spacing = _grantClearance ? 16 : 10 };

        foreach (var (apiName, displayName) in TerminalUI.ResourceAccessTypes)
        {
            // Caller filtering — skip types the caller has no grants for
            var callerIds = GetCallerResourceIds(apiName);
            if (_callerFilter is not null && callerIds is null)
                continue;

            var section = new StackPanel { Spacing = _grantClearance ? 4 : 2 };
            var header = new TextBlock
            {
                Text = displayName, FontFamily = TerminalUI.Mono, FontSize = 12,
                Foreground = TerminalUI.Brush(0x00CCFF),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            if (TerminalUI.ResourceAccessTooltips.TryGetValue(apiName, out var resTip))
                ToolTipService.SetToolTip(header, resTip);
            section.Children.Add(header);

            var gp = new StackPanel { Spacing = _grantClearance ? 6 : 2, Margin = new Thickness(12, 0, 0, 0) };
            _grantPanels[apiName] = gp;

            // Populate existing grants
            if (_existing is { } ex && ex.TryGetProperty(apiName, out var ap) && ap.ValueKind == JsonValueKind.Array)
                foreach (var g in ap.EnumerateArray())
                    if (g.TryGetProperty("resourceId", out var rid) && rid.ValueKind == JsonValueKind.String)
                    {
                        var cl = g.TryGetProperty("clearance", out var clp) ? clp.GetString() ?? "Independent" : "Independent";
                        AddGrantRow(gp, rid.GetGuid(), cl);
                    }

            section.Children.Add(gp);

            // Add-resource controls
            var capturedApi = apiName;
            var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            if (_useAutoSuggest)
                BuildAutoSuggestSelector(actionsRow, capturedApi, apiName);
            else
                BuildComboSelector(actionsRow, capturedApi, callerIds);

            section.Children.Add(actionsRow);
            resCont.Children.Add(section);
        }

        container.Children.Add(resCont);
    }

    // ── Collection methods ───────────────────────────────────────

    /// <summary>Reads the current state of global flag checkboxes (+ optional clearance).</summary>
    public Dictionary<string, object?> CollectFlags()
    {
        var req = new Dictionary<string, object?>();
        for (var i = 0; i < TerminalUI.GlobalFlagNames.Length; i++)
        {
            var flag = TerminalUI.GlobalFlagNames[i];
            if (!_flagEditors.TryGetValue(flag, out var ed)) continue;
            req[flag] = ed.Check.IsChecked == true;
            if (_flagClearance && ed.Clearance is { } clr)
                req[TerminalUI.GlobalFlagClearanceNames[i]] =
                    clr.SelectedItem is ComboBoxItem { Tag: string cl } ? cl : "Unset";
        }
        return req;
    }

    /// <summary>Reads the current state of per-resource grant rows.</summary>
    public Dictionary<string, object?> CollectGrants()
    {
        var req = new Dictionary<string, object?>();
        foreach (var (apiName, panel) in _grantPanels)
        {
            var grants = new List<object>();
            foreach (var child in panel.Children)
            {
                if (child is not StackPanel row) continue;
                var idBlock = row.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Tag is Guid);
                if (idBlock?.Tag is not Guid resId) continue;

                string clearance;
                if (_grantClearance)
                {
                    var clrBox = row.Children.OfType<ComboBox>().FirstOrDefault();
                    clearance = clrBox?.SelectedItem is ComboBoxItem { Tag: string cl } ? cl : "Unset";
                }
                else
                {
                    clearance = "Independent";
                }

                grants.Add(new { resourceId = resId, clearance });
            }
            req[apiName] = grants;
        }
        return req;
    }

    /// <summary>Merges <see cref="CollectFlags"/> and <see cref="CollectGrants"/> into one dictionary.</summary>
    public Dictionary<string, object?> CollectAll()
    {
        var result = CollectFlags();
        foreach (var (k, v) in CollectGrants()) result[k] = v;
        return result;
    }

    // ── Private helpers ─────────────────────────────────────────

    private void AddGrantRow(StackPanel panel, Guid resId, string clearance)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = _grantClearance ? 10 : 8 };

        string idText;
        if (resId == TerminalUI.AllResourcesId)
            idText = "* (all)";
        else if (_nameCache.TryGetValue(resId, out var name))
            idText = _useAutoSuggest ? $"{name}  ({resId.ToString()[..8]}\u2026)" : name;
        else
            idText = resId.ToString()[..8] + "\u2026";

        var idBlock = new TextBlock
        {
            Text = idText, FontFamily = TerminalUI.Mono, FontSize = 11,
            Foreground = TerminalUI.Brush(0xE0E0E0), VerticalAlignment = VerticalAlignment.Center,
            MinWidth = _grantClearance ? 140 : 80, Tag = resId,
        };
        if (resId != TerminalUI.AllResourcesId) ToolTipService.SetToolTip(idBlock, resId.ToString());
        row.Children.Add(idBlock);

        if (_grantClearance)
        {
            row.Children.Add(new TextBlock
            {
                Text = "Clearance:", FontFamily = TerminalUI.Mono, FontSize = 9,
                Foreground = TerminalUI.Brush(0x808080), VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(TerminalUI.MakeClearanceCombo(clearance));
        }

        var rm = TerminalUI.RemoveButton(() => panel.Children.Remove(row));
        rm.Padding = new Thickness(2);
        if (rm.Content is TextBlock rmTb) rmTb.FontSize = 9;
        row.Children.Add(rm);
        panel.Children.Add(row);
    }

    private void BuildAutoSuggestSelector(StackPanel actionsRow, string capturedApi, string apiName)
    {
        // Wildcard button
        var addWc = new Button
        {
            Content = new TextBlock { Text = "+ wildcard", FontFamily = TerminalUI.Mono, FontSize = 10, Foreground = TerminalUI.Brush(0x00FF00) },
            Background = TerminalUI.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 2, 4, 2), MinWidth = 0, MinHeight = 0,
        };
        addWc.Click += (_, _) =>
        {
            if (_grantPanels.TryGetValue(capturedApi, out var panel))
                AddGrantRow(panel, TerminalUI.AllResourcesId, "Independent");
        };
        actionsRow.Children.Add(addWc);

        // AutoSuggestBox
        if (_resourcesByType.TryGetValue(apiName, out var lookupItems) && lookupItems.Count > 0)
        {
            var resDisplayMap = new Dictionary<string, Guid>(lookupItems.Count);
            foreach (var (id, name) in lookupItems)
                resDisplayMap.TryAdd($"{name}  ({id.ToString()[..8]}\u2026)", id);
            var resSearch = new AutoSuggestBox
            {
                PlaceholderText = "+ add resource\u2026", FontFamily = TerminalUI.Mono,
                FontSize = 10, MinWidth = 200,
            };
            ToolTipService.SetToolTip(resSearch, "Search for a specific resource to grant access to");
            resSearch.TextChanged += (sender, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                var q = sender.Text.Trim();
                sender.ItemsSource = string.IsNullOrEmpty(q)
                    ? resDisplayMap.Keys.ToList()
                    : resDisplayMap.Keys.Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            };
            resSearch.QuerySubmitted += (sender, args) =>
            {
                var chosen = args.ChosenSuggestion?.ToString();
                if (chosen is null || !resDisplayMap.TryGetValue(chosen, out var resId)) return;
                sender.Text = string.Empty;
                if (_grantPanels.TryGetValue(capturedApi, out var panel))
                    AddGrantRow(panel, resId, "Independent");
            };
            actionsRow.Children.Add(resSearch);
        }
    }

    private void BuildComboSelector(StackPanel actionsRow, string capturedApi, HashSet<Guid>? callerIds)
    {
        actionsRow.Children.Add(new TextBlock
        {
            Text = "Resource:", FontFamily = TerminalUI.Mono, FontSize = 10,
            Foreground = TerminalUI.Brush(0x00CCFF), VerticalAlignment = VerticalAlignment.Center,
        });
        var resSelector = new ComboBox
        {
            FontFamily = TerminalUI.Mono, FontSize = 10,
            Background = TerminalUI.Brush(0x0A1A2A), Foreground = TerminalUI.Brush(0x00CCFF),
            BorderBrush = TerminalUI.Brush(0x00CCFF), BorderThickness = new Thickness(1),
            MinWidth = 220,
        };
        PopulateResourceSelector(resSelector, capturedApi, callerIds);
        var addBtn = new Button
        {
            Content = new TextBlock { Text = "+ add", FontFamily = TerminalUI.Mono, FontSize = 10, Foreground = TerminalUI.Brush(0x00FF00) },
            Background = TerminalUI.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2), MinWidth = 0, MinHeight = 0,
        };
        addBtn.Click += (_, _) =>
        {
            if (resSelector.SelectedItem is not ComboBoxItem { Tag: Guid resId } || resId == Guid.Empty) return;
            if (!_grantPanels.TryGetValue(capturedApi, out var panel)) return;
            foreach (var child in panel.Children)
                if (child is StackPanel r && r.Children.Count > 0
                    && r.Children[0] is TextBlock { Tag: Guid existing } && existing == resId)
                    return;
            AddGrantRow(panel, resId, "Independent");
        };
        actionsRow.Children.Add(resSelector);
        actionsRow.Children.Add(addBtn);
    }

    private async Task PreloadResourceNamesAsync()
    {
        var tasks = TerminalUI.ResourceAccessTypes.Select(async r =>
        {
            try
            {
                using var resp = await _api.GetAsync($"/resources/lookup/{r.ApiName}");
                if (!resp.IsSuccessStatusCode) return;
                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);
                var items = new List<(Guid Id, string Name)>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetGuid();
                    var name = item.GetProperty("name").GetString() ?? id.ToString()[..8];
                    _nameCache[id] = name;
                    items.Add((id, name));
                }
                _resourcesByType[r.ApiName] = items;
            }
            catch { /* swallow */ }
        });
        await Task.WhenAll(tasks);
    }

    private HashSet<Guid>? GetCallerResourceIds(string accessType)
    {
        if (_callerFilter is not { } perms) return null;
        if (!perms.TryGetProperty(accessType, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var ids = new HashSet<Guid>();
        foreach (var g in arr.EnumerateArray())
            if (g.TryGetProperty("resourceId", out var rid) && rid.ValueKind == JsonValueKind.String)
                ids.Add(rid.GetGuid());
        return ids.Count > 0 ? ids : null;
    }

    private void PopulateResourceSelector(ComboBox selector, string accessType, HashSet<Guid>? callerIds)
    {
        // If caller has no grants for this type, show disabled placeholder
        if (_callerFilter is not null && callerIds is null)
        {
            selector.Items.Add(new ComboBoxItem { Content = "(no access)", Tag = Guid.Empty, IsEnabled = false });
            selector.SelectedIndex = 0;
            return;
        }

        var hasWildcard = callerIds is not null && callerIds.Contains(TerminalUI.AllResourcesId);

        if (callerIds is null || hasWildcard)
            selector.Items.Add(new ComboBoxItem { Content = "* (all resources)", Tag = TerminalUI.AllResourcesId });

        if (_resourcesByType.TryGetValue(accessType, out var items))
            foreach (var (id, name) in items)
                if (callerIds is null || hasWildcard || callerIds.Contains(id))
                    selector.Items.Add(new ComboBoxItem { Content = name, Tag = id });

        if (selector.Items.Count > 0)
            selector.SelectedIndex = 0;
    }
}
